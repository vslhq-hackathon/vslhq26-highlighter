using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/reclip.py.
///
/// Cut a new clip from a project's archived source video in S3.
///
/// Looks up the project's source_archive pointer, downloads the segments covering
/// the requested window, renders an MP4, uploads it to the clips bucket, and
/// inserts a clips row — the full S3 -> Supabase round trip, long after the
/// original stream ended.</summary>
public static class Reclip
{
    public static void Main(string[] argv)
    {
        Config.LoadEnv();
        var positionals = new List<string>();
        string? title = null;
        string? description = null;
        var clipsBucket = Defaults.DEFAULT_SUPABASE_CLIPS_BUCKET;
        for (var i = 0; i < argv.Length; i++)
        {
            var (flag, inlineValue) = Argv.SplitFlag(argv[i]);
            string Next() => inlineValue ?? Argv.NextValue(argv, ref i, flag);
            switch (flag)
            {
                case "--title":
                    title = Next();
                    break;
                case "--description":
                    description = Next();
                    break;
                case "--clips-bucket":
                    clipsBucket = Next();
                    break;
                default:
                    Argv.Positional(flag, positionals);
                    break;
            }
        }
        if (positionals.Count != 3)
            throw new PipelineError(
                "the following arguments are required: project_id, start_seconds, end_seconds");
        var projectId = positionals[0];
        var startSeconds = Argv.Float(positionals[1], "start_seconds");
        var endSeconds = Argv.Float(positionals[2], "end_seconds");

        if (endSeconds <= startSeconds)
            throw new PipelineError("end must be greater than start");

        var db = new SupabaseClient();
        var project = db.GetProject(projectId);
        var archive = (project["metadata"] as JsonObject)?["source_archive"] as JsonObject;
        if (archive is null || !JsonUtil.Truthy(archive))
        {
            throw new PipelineError(
                $"Project {projectId} has no source_archive; only archived livestreams can be re-clipped.");
        }

        var segmentSeconds = JsonUtil.Int(archive["segment_seconds"]);
        var firstIndex = (int)(startSeconds / segmentSeconds);
        var lastIndex = (int)(Math.Max(startSeconds, endSeconds - 0.001) / segmentSeconds);
        // Editing-master handles: also pull neighbor segments the archive is
        // known to hold, so a reclip ships with the same ±handle extension room
        // as ingest-rendered clips (pads shrink when the archive ends).
        var handle = Defaults.DEFAULT_CLIP_HANDLE_SECONDS;
        var padFirst = (int)(Math.Max(0, startSeconds - handle) / segmentSeconds);
        var padLast = (int)(Math.Max(startSeconds, endSeconds + handle - 0.001) / segmentSeconds);
        if (padFirst < firstIndex && !HasKnownSegment(archive, padFirst)) padFirst = firstIndex;
        if (padLast > lastIndex && !HasKnownSegment(archive, padLast)) padLast = lastIndex;
        var keys = SegmentKeys(archive, firstIndex: padFirst, lastIndex: padLast);

        var storage = new S3Storage(
            JsonUtil.Str(archive["bucket"]),
            JsonUtil.Truthy(archive["region"])
                ? JsonUtil.Str(archive["region"])
                : Config.Env("AWS_REGION", Defaults.DEFAULT_AWS_REGION));
        var clipTitle = title ?? $"Reclip {Py.G(startSeconds)}s-{Py.G(endSeconds)}s";
        var filename = Render.ClipFilename(
            chunkIndex: firstIndex,
            startSeconds: startSeconds,
            endSeconds: endSeconds);

        var outputRoot = Config.Env("OUTPUT_ROOT", Defaults.DEFAULT_OUTPUT_ROOT);
        var projectDir = Path.Combine(outputRoot, "projects", projectId);
        var outputPath = Path.Combine(projectDir, "clips", filename);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var masterRender = new JsonObject();
        using (var tmpdir = new TempDir(parent: Path.GetDirectoryName(Path.GetFullPath(outputPath))))
        {
            var segmentPaths = new List<string>();
            foreach (var key in keys)
            {
                var local = Path.Combine(tmpdir.Path, Path.GetFileName(key));
                Console.WriteLine($"Downloading s3://{storage.Bucket}/{key}");
                storage.DownloadFile(key, local);
                segmentPaths.Add(local);
            }

            Console.WriteLine($"Rendering {filename} from {segmentPaths.Count} segment(s)");
            Render.RenderClipFromSegments(
                segmentPaths: segmentPaths,
                outputPath: outputPath,
                firstSegmentStartSeconds: padFirst * (double)segmentSeconds,
                startSeconds: startSeconds,
                endSeconds: endSeconds,
                profile: EncodeProfile.Delivery);

            try
            {
                var masterStart = Math.Max(
                    padFirst * (double)segmentSeconds, Math.Max(0, startSeconds - handle));
                var masterFilename = Path.GetFileNameWithoutExtension(filename) + "_master.mp4";
                var masterPath = Path.Combine(Path.GetDirectoryName(outputPath)!, masterFilename);
                Render.RenderClipFromSegments(
                    segmentPaths: segmentPaths,
                    outputPath: masterPath,
                    firstSegmentStartSeconds: padFirst * (double)segmentSeconds,
                    startSeconds: masterStart,
                    endSeconds: endSeconds + handle,
                    profile: EncodeProfile.Delivery);
                var masterDuration = Render.ProbeDuration(masterPath)
                    ?? (endSeconds + handle - masterStart);
                masterRender["master_filename"] = masterFilename;
                masterRender["master_local_path"] =
                    Path.GetRelativePath(Directory.GetCurrentDirectory(), masterPath);
                masterRender["master_pad_start"] =
                    Py.Round(Math.Max(0, startSeconds - masterStart), 3);
                masterRender["master_pad_end"] = Py.Round(
                    Math.Max(0, masterDuration - (endSeconds - masterStart)), 3);
                masterRender["master_duration_seconds"] = Py.Round(masterDuration, 3);
            }
            catch (Exception exc)
            {
                Console.WriteLine($"Editing-master render failed (non-fatal): {exc.Message}");
            }
        }

        var storageKey = $"projects/{projectId}/clips/{filename}";
        var videoUrl = Uploads.UploadFittedOrLocalUrl(
            db,
            bucket: clipsBucket,
            key: storageKey,
            path: outputPath,
            label: "Reclip",
            durationSeconds: endSeconds - startSeconds);
        var render = new JsonObject
        {
            ["status"] = "rendered",
            ["bucket"] = clipsBucket,
            ["storage_path"] = storageKey,
            ["video_url"] = videoUrl,
            ["filename"] = filename,
            ["segment_keys"] = JsonUtil.Arr(keys.Select(key => (JsonNode?)key)),
        };
        if (Render.ProbeFps(outputPath) is { } fps) render["fps"] = Py.Round(fps, 3);
        if (JsonUtil.StrOrNull(masterRender["master_filename"]) is { } masterFile)
        {
            foreach (var (key, value) in masterRender.ToList())
                render[key] = value?.DeepClone();
            var masterKey = $"projects/{projectId}/clips/{masterFile}";
            render["master_url"] = Uploads.UploadFittedOrLocalUrl(
                db, bucket: clipsBucket, key: masterKey,
                path: Path.Combine(Path.GetDirectoryName(outputPath)!, masterFile),
                label: "Editing master",
                durationSeconds: JsonUtil.Double(masterRender["master_duration_seconds"]));
            render["master_storage_path"] = masterKey;
        }
        db.InsertClip(
            projectId: projectId,
            title: clipTitle,
            description: description,
            startSeconds: startSeconds,
            endSeconds: endSeconds,
            videoUrl: videoUrl,
            status: "rendered",
            metadata: new JsonObject
            {
                ["source"] = "reclip",
                ["render"] = render,
            });
        Console.WriteLine($"Clip stored: {videoUrl}");
        Console.WriteLine($"Local copy: {outputPath}");
    }

    /// <summary>True when the archive's recorded keys include this segment.
    /// Prefix-only archives can't answer, so padding stays off for them rather
    /// than gambling on a download that may 404.</summary>
    private static bool HasKnownSegment(JsonObject archive, int index)
    {
        foreach (var node in archive["segment_keys"] as JsonArray ?? new JsonArray())
        {
            var match = Regex.Match(JsonUtil.Str(node), @"video_(\d+)\.ts$");
            if (match.Success && int.Parse(match.Groups[1].Value) == index) return true;
        }
        return false;
    }

    private static List<string> SegmentKeys(JsonObject archive, int firstIndex, int lastIndex)
    {
        var knownKeys = (archive["segment_keys"] as JsonArray ?? new JsonArray())
            .Select(node => JsonUtil.Str(node))
            .ToList();
        var byIndex = new Dictionary<int, string>();
        foreach (var key in knownKeys)
        {
            var match = Regex.Match(key, @"video_(\d+)\.ts$");
            if (match.Success)
                byIndex[int.Parse(match.Groups[1].Value)] = key;
        }

        var keys = new List<string>();
        for (var index = firstIndex; index <= lastIndex; index++)
        {
            if (byIndex.TryGetValue(index, out var key))
            {
                keys.Add(key);
            }
            else if (knownKeys.Count == 0 && JsonUtil.Truthy(archive["prefix"]))
            {
                // Older projects recorded only a prefix; segment names are deterministic.
                keys.Add($"{JsonUtil.Str(archive["prefix"]).TrimEnd('/')}/video_{index:00000}.ts");
            }
            else
            {
                throw new PipelineError(
                    $"Requested window needs archive segment {index}, which is not in this project's archive.");
            }
        }
        return keys;
    }
}
