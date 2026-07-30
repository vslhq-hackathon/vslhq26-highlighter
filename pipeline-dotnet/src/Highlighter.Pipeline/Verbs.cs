using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Ports of the standalone project verbs: thumbnails regeneration
/// (highlighter_pipeline/thumbnails.py main), research rerun (research.py
/// main), and clip reformatting (reformat.py).</summary>
public static class Verbs
{
    public const int SQUARE_SIZE = 720;

    private static (string ProjectDir, SupabaseClient? Db, ProjectRecords Records) OpenProject(
        string projectId, string? outputRoot)
    {
        var root = outputRoot
            ?? Config.Env("OUTPUT_ROOT", Defaults.DEFAULT_OUTPUT_ROOT)
            ?? Defaults.DEFAULT_OUTPUT_ROOT;
        var projectDir = Path.Combine(root, "projects", projectId);
        if (!File.Exists(Path.Combine(projectDir, "project.json")))
            throw new PipelineError($"No local project record at {projectDir}");
        var db = projectId.StartsWith("local-") ? null : new SupabaseClient();
        return (projectDir, db, new ProjectRecords(projectDir));
    }

    private static List<JsonObject> ReadJsonl(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path)
                .Where(line => line.Trim().Length > 0)
                .Select(line => JsonUtil.ParseObject(line))
                .ToList()
            : new List<JsonObject>();

    // ------------------------------------------------------------------ //
    // highlighter thumbnails
    // ------------------------------------------------------------------ //

    public static void ThumbnailsMain(string[] argv)
    {
        Config.LoadEnv();
        string? projectId = null, prompt = null, outputRoot = null;
        int? version = null, select = null;
        var positionals = new List<string>();
        for (var i = 0; i < argv.Length; i++)
        {
            var (flag, inline) = Argv.SplitFlag(argv[i]);
            switch (flag)
            {
                case "--prompt": prompt = inline ?? Argv.NextValue(argv, ref i, flag); break;
                case "--version":
                    version = int.Parse(inline ?? Argv.NextValue(argv, ref i, flag)); break;
                case "--select":
                    select = int.Parse(inline ?? Argv.NextValue(argv, ref i, flag)); break;
                case "--output-root": outputRoot = inline ?? Argv.NextValue(argv, ref i, flag); break;
                default: Argv.Positional(argv[i], positionals); break;
            }
        }
        if (positionals.Count != 1)
            throw new PipelineError("usage: highlighter thumbnails <project-id> [--prompt ...]");
        projectId = positionals[0];

        var (projectDir, db, records) = OpenProject(projectId, outputRoot);
        var rows = ReadJsonl(Path.Combine(projectDir, "longform_edits.jsonl"));
        if (rows.Count == 0)
            throw new PipelineError("No long-form edit on record for this project");
        var row = version is int wanted
            ? rows.LastOrDefault(r => (int)JsonUtil.DoubleOr(r["version"], 1) == wanted)
                ?? throw new PipelineError($"No long-form version {wanted} on record")
            : rows.OrderBy(r => JsonUtil.DoubleOr(r["version"], 1)).Last();
        var rowVersion = (int)JsonUtil.DoubleOr(row["version"], 1);
        var metadata = row["metadata"] as JsonObject ?? new JsonObject();
        var render = metadata["render"] as JsonObject ?? new JsonObject();
        var thumbnails = render["thumbnails"] as JsonObject
            ?? new JsonObject { ["variants"] = new JsonArray(), ["selected_index"] = 0 };
        var variants = thumbnails["variants"] as JsonArray ?? new JsonArray();

        if (select is int selectIndex)
        {
            var chosen = variants.OfType<JsonObject>()
                .FirstOrDefault(v =>
                    (int)JsonUtil.DoubleOr(v["index"], 0) == selectIndex
                    && JsonUtil.Truthy(v["url"]));
            if (chosen is null)
                throw new PipelineError($"No hosted thumbnail #{selectIndex} on record");
            thumbnails["selected_index"] = variants.IndexOf(chosen);
            render["thumbnails"] = thumbnails;
            PersistRender(db, records, row, rowVersion, metadata, render, JsonUtil.Str(chosen["url"]));
            Console.WriteLine($"Selected thumbnail #{selectIndex} for version {rowVersion}");
            return;
        }

        var longformPath = Path.Combine(
            projectDir, "longform", JsonUtil.StrOrNull(render["filename"]) ?? "longform.mp4");
        if (!File.Exists(longformPath))
            throw new PipelineError($"Long-form video not found at {longformPath}");
        var researchPath = Path.Combine(projectDir, "research.json");
        var research = File.Exists(researchPath)
            ? JsonUtil.ParseObject(File.ReadAllText(researchPath))
            : null;
        var entries = (render["segment_windows"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>().ToList();
        var startIndex = variants.OfType<JsonObject>()
            .Select(v => (int)JsonUtil.DoubleOr(v["index"], 0))
            .DefaultIfEmpty(0)
            .Max() + 1;

        var result = Thumbnails.GenerateThumbnails(
            longformPath,
            entries,
            JsonUtil.StrOrNull(render["title"]),
            research,
            Path.Combine(projectDir, "thumbnails"),
            guidance: prompt,
            startIndex: startIndex)
            ?? throw new PipelineError("No thumbnails were generated");

        var bucket = Config.Env("SUPABASE_CLIPS_BUCKET", Defaults.DEFAULT_SUPABASE_CLIPS_BUCKET)!;
        foreach (var variant in (result["variants"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (db is null) continue;
            var localPath = JsonUtil.Str(variant["local_path"]);
            var key = $"projects/{projectId}/thumbnails/{Path.GetFileName(localPath)}";
            try
            {
                variant["url"] = db.UploadStorageObject(bucket, key, localPath);
                variant["storage_path"] = key;
            }
            catch (Exception exc)
            {
                Console.WriteLine($"Thumbnail upload failed (kept locally at {localPath}): {exc.Message}");
            }
        }

        var merged = new JsonArray();
        foreach (var variant in variants.OfType<JsonObject>().ToList()) merged.Add(JsonUtil.CloneObj(variant));
        foreach (var variant in (result["variants"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList())
            merged.Add(JsonUtil.CloneObj(variant));
        thumbnails["variants"] = merged;
        render["thumbnails"] = thumbnails;
        PersistRender(db, records, row, rowVersion, metadata, render, thumbnailUrl: null);
        var added = (result["variants"] as JsonArray)?.Count ?? 0;
        Console.WriteLine(
            $"Added {added} thumbnail variant(s) to version {rowVersion}; "
            + $"the set now has {merged.Count}.");
    }

    private static void PersistRender(
        SupabaseClient? db,
        ProjectRecords records,
        JsonObject row,
        int version,
        JsonObject metadata,
        JsonObject render,
        string? thumbnailUrl)
    {
        metadata["render"] = render;
        row["metadata"] = metadata;
        if (!string.IsNullOrEmpty(thumbnailUrl)) row["thumbnail_url"] = thumbnailUrl;
        records.UpdateLongformEdit(version, row);
        db?.UpdateLongformEdit(
            JsonUtil.StrOrNull(row["project_id"]) ?? "",
            version,
            thumbnailUrl,
            metadata);
    }

    // ------------------------------------------------------------------ //
    // highlighter research
    // ------------------------------------------------------------------ //

    public static void ResearchMain(string[] argv)
    {
        Config.LoadEnv();
        string mode = "long";
        string? focus = null, outputRoot = null;
        var positionals = new List<string>();
        for (var i = 0; i < argv.Length; i++)
        {
            var (flag, inline) = Argv.SplitFlag(argv[i]);
            switch (flag)
            {
                case "--mode":
                    mode = Argv.Choice(
                        inline ?? Argv.NextValue(argv, ref i, flag), flag, "short", "long");
                    break;
                case "--focus": focus = inline ?? Argv.NextValue(argv, ref i, flag); break;
                case "--output-root": outputRoot = inline ?? Argv.NextValue(argv, ref i, flag); break;
                default: Argv.Positional(argv[i], positionals); break;
            }
        }
        if (positionals.Count != 1)
            throw new PipelineError("usage: highlighter research <project-id> [--mode short|long]");
        var projectId = positionals[0];

        var (projectDir, db, records) = OpenProject(projectId, outputRoot);
        var project = JsonUtil.ParseObject(
            File.ReadAllText(Path.Combine(projectDir, "project.json")));
        var metadata = project["metadata"] as JsonObject ?? new JsonObject();
        var ingest = metadata["ingest"] as JsonObject ?? new JsonObject();

        var instructions = string.Join("\n", new[]
        {
            JsonUtil.StrOrNull(ingest["user_instructions"]),
            focus is null ? null : $"Research focus: {focus}",
        }.Where(part => !string.IsNullOrEmpty(part)));
        var context = Research.ResearchContentContext(
            sourceContext: new JsonObject
            {
                ["platform"] = JsonUtil.C(metadata["platform"]),
                ["source_type"] = JsonUtil.C(project["source_type"]),
                ["name"] = JsonUtil.C(project["name"]),
                ["source_url"] = JsonUtil.C(project["source_url"]),
            },
            pipelineMode: mode,
            userInstructions: instructions.Length > 0 ? instructions : null);
        records.WriteResearch(context, mode == "short" ? "short" : null);

        var key = mode == "short" ? "content_research_short" : "content_research";
        var updatedMetadata = JsonUtil.CloneObj(metadata);
        updatedMetadata[key] = JsonUtil.CloneObj(context);
        records.UpdateProject(("metadata", updatedMetadata));
        if (db is not null)
        {
            var remote = db.GetProject(projectId)["metadata"] as JsonObject ?? new JsonObject();
            var remoteMerged = JsonUtil.CloneObj(remote);
            remoteMerged[key] = JsonUtil.CloneObj(context);
            db.UpdateProjectMetadata(projectId, remoteMerged);
        }
        var sources = (context["sources"] as JsonArray)?.Count ?? 0;
        Console.WriteLine($"Refreshed {mode}-form research with {sources} source(s)");
    }

    // ------------------------------------------------------------------ //
    // highlighter reformat
    // ------------------------------------------------------------------ //

    /// <summary>A centered full-height square crop scaled to SQUARE_SIZE.</summary>
    public static void RenderCenterCropSquare(string clipPath, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var (code, stdout, stderr) = Proc.Run(new[]
        {
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-i", clipPath,
            "-vf", $"crop=ih:ih,scale={SQUARE_SIZE}:{SQUARE_SIZE},setsar=1",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-c:a", "aac", "-b:a", "128k",
            "-movflags", "+faststart",
            outputPath,
        });
        if (code != 0)
        {
            var details = (stderr.Length > 0 ? stderr : stdout).Trim();
            throw new PipelineError(details.Length > 0 ? details : $"ffmpeg exited with {code}");
        }
    }

    public static void ReformatMain(string[] argv)
    {
        Config.LoadEnv();
        string? outputRoot = null;
        var captions = false;
        var positionals = new List<string>();
        for (var i = 0; i < argv.Length; i++)
        {
            var (flag, inline) = Argv.SplitFlag(argv[i]);
            switch (flag)
            {
                case "--format":
                    Argv.Choice(inline ?? Argv.NextValue(argv, ref i, flag), flag, "square");
                    break;
                case "--captions": captions = true; break;
                case "--output-root": outputRoot = inline ?? Argv.NextValue(argv, ref i, flag); break;
                default: Argv.Positional(argv[i], positionals); break;
            }
        }
        if (positionals.Count != 2)
            throw new PipelineError(
                "usage: highlighter reformat <project-id> <clip-filename> [--format square] [--captions]");
        var projectId = positionals[0];
        var clipFilename = positionals[1];

        var (projectDir, db, records) = OpenProject(projectId, outputRoot);
        var rows = ReadJsonl(Path.Combine(projectDir, "clips.jsonl"));
        var row = rows.FirstOrDefault(r =>
                JsonUtil.StrOrNull((r["metadata"]?["render"] as JsonObject)?["filename"])
                == clipFilename)
            ?? throw new PipelineError($"No clip named {clipFilename} on record");
        var metadata = row["metadata"] as JsonObject ?? new JsonObject();
        var render = metadata["render"] as JsonObject ?? new JsonObject();

        var clipPath = Path.Combine(projectDir, "clips", clipFilename);
        if (!File.Exists(clipPath))
            throw new PipelineError($"Clip file not found at {clipPath}");

        var squarePath = Path.Combine(
            Path.GetDirectoryName(clipPath)!,
            $"{Path.GetFileNameWithoutExtension(clipPath)}_square.mp4");
        RenderCenterCropSquare(clipPath, squarePath);
        render["square_path"] = Path.GetRelativePath(Directory.GetCurrentDirectory(), squarePath);
        Console.WriteLine($"Rendered square crop to {squarePath}");

        string? captionedPath = null;
        if (captions)
        {
            if (!Captions.CaptionsAvailable())
            {
                Console.WriteLine(
                    "Captions unavailable (pycaps not on PATH); shipping the clean square only.");
            }
            else
            {
                var words = new JsonArray();
                foreach (var chunk in ReadJsonl(Path.Combine(projectDir, "transcript_chunks.jsonl")))
                    foreach (var word in chunk["words"] as JsonArray ?? new JsonArray())
                        if (word is JsonObject w) words.Add(JsonUtil.CloneObj(w));
                captionedPath = Captions.CaptionClip(
                    squarePath,
                    words,
                    JsonUtil.Double(row["start_seconds"]),
                    JsonUtil.Double(row["end_seconds"]),
                    Path.Combine(
                        Path.GetDirectoryName(squarePath)!,
                        $"{Path.GetFileNameWithoutExtension(squarePath)}_captions.mp4"));
                if (captionedPath is not null)
                {
                    render["square_captioned_path"] =
                        Path.GetRelativePath(Directory.GetCurrentDirectory(), captionedPath);
                    Console.WriteLine($"Captioned square to {captionedPath}");
                }
            }
        }

        if (db is not null)
        {
            var bucket = Config.Env(
                "SUPABASE_CLIPS_BUCKET", Defaults.DEFAULT_SUPABASE_CLIPS_BUCKET)!;
            var clipSeconds = JsonUtil.Double(row["end_seconds"])
                - JsonUtil.Double(row["start_seconds"]);
            var fields = new JsonObject();
            try
            {
                var key = $"projects/{projectId}/clips/{Path.GetFileName(squarePath)}";
                render["square_url"] = Uploads.UploadFittedOrLocalUrl(
                    db, bucket, key, squarePath, label: "Square clip",
                    durationSeconds: clipSeconds);
                render["square_storage_path"] = key;
                if (captionedPath is not null)
                {
                    var captionedKey = $"projects/{projectId}/clips/{Path.GetFileName(captionedPath)}";
                    render["square_captioned_url"] = Uploads.UploadFittedOrLocalUrl(
                        db, bucket, captionedKey, captionedPath, label: "Square captioned clip",
                        durationSeconds: clipSeconds);
                    render["square_captioned_storage_path"] = captionedKey;
                }
                var mergedMetadata = JsonUtil.CloneObj(metadata);
                mergedMetadata["render"] = JsonUtil.CloneObj(render);
                fields["metadata"] = mergedMetadata;
            }
            catch (Exception exc)
            {
                Console.WriteLine($"Square upload failed (kept locally): {exc.Message}");
            }
            if (fields.Count > 0) db.UpdateClipMedia(projectId, clipFilename, fields);
        }

        metadata["render"] = render;
        row["metadata"] = metadata;
        records.UpdateClip(clipFilename, row);
        Console.WriteLine($"Recorded square media for {clipFilename}");
    }
}
