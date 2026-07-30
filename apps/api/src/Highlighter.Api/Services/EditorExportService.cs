using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;

namespace Highlighter.Api.Services;

/// <summary>Runs editor exports as in-process jobs: fetch the source media,
/// render the EDL with ffmpeg, grab a thumbnail, upload both to the public
/// bucket, and record the result — clips get metadata.editor.render (a
/// deterministic, overwritten path), long-form edits get a brand-new version
/// row whose metadata.render the existing DB cleanup trigger understands.</summary>
public sealed class EditorExportService(
    RepoLayout layout,
    SupabaseDb db,
    SupabaseStorage storage,
    EditorRenderer renderer,
    IHttpClientFactory httpFactory,
    PipelineJobService jobs,
    ILogger<EditorExportService> log)
{
    public const string HttpClientName = "media-download";

    // Supabase rejects objects over the project cap (measured: 50 MiB) with a
    // 413. Twin of Uploads.MaxStorageUploadBytes in pipeline-dotnet — the API
    // deliberately has no project reference there, so the env var is the shared
    // contract. Exports must FIT (re-encode), not fall back to /media: the work
    // dir is deleted after the job.
    private const long DefaultMaxUploadBytes = 49L * 1024 * 1024;

    private static long MaxUploadBytes()
    {
        var raw = Environment.GetEnvironmentVariable("HIGHLIGHTER_MAX_UPLOAD_BYTES");
        return raw is not null && long.TryParse(raw, out var value) && value > 0
            ? value
            : DefaultMaxUploadBytes;
    }

    public PipelineJob StartClipExport(Guid projectId, ClipDto clip, EditorDoc doc,
        MasterInfo? master = null, Guid? ownerId = null) =>
        jobs.StartExternal("editor-export", projectId,
            ["editor-export", "clip", clip.Id.ToString()],
            (job, ct) => RunClipExportAsync(job, projectId, clip, doc, master, ct), ownerId);

    public PipelineJob StartLongformExport(Guid projectId, JsonObject editRow, EditorDoc doc,
        Guid? ownerId = null) =>
        jobs.StartExternal("editor-export", projectId,
            ["editor-export", "longform",
             $"v{(int)(editRow["version"]?.GetValue<double>() ?? 1)}"],
            (job, ct) => RunLongformExportAsync(job, projectId, editRow, doc, ct), ownerId);

    // ---- clip ------------------------------------------------------------ //

    private async Task<int> RunClipExportAsync(
        PipelineJob job, Guid projectId, ClipDto clip, EditorDoc doc, MasterInfo? master,
        CancellationToken ct)
    {
        var work = WorkDir(job.Id);
        try
        {
            // The doc's source seconds run against the padded master when one
            // exists (handle material is legal timeline content); prefer the
            // local file when this machine rendered it.
            var masterLocal = master?.LocalPath is { } relative
                ? Path.Combine(layout.RepoRoot, relative)
                : null;
            var source = masterLocal is not null && File.Exists(masterLocal)
                ? masterLocal
                : master?.Url ?? clip.VerticalUrl ?? clip.VideoUrl ?? clip.CaptionedUrl
                    ?? throw new InvalidOperationException("the clip has no rendered media to edit");
            var (outputPath, thumbPath, outputDuration) =
                await RenderAsync(job, work, source, doc, ct);

            var videoKey = $"{projectId}/editor/clip_{clip.Id}.mp4";
            var thumbKey = $"{projectId}/editor/clip_{clip.Id}.jpg";
            job.Append("api", $"uploading {videoKey}");
            await storage.UploadFileAsync(videoKey, outputPath, "video/mp4", ct);
            await storage.UploadFileAsync(thumbKey, thumbPath, "image/jpeg", ct);

            await new EditorStore(db).SaveClipExportAsync(projectId, clip.Id, new JsonObject
            {
                ["bucket"] = storage.Bucket,
                ["storage_path"] = videoKey,
                ["thumbnail_storage_path"] = thumbKey,
                ["url"] = storage.PublicUrl(videoKey),
                ["thumbnail_url"] = storage.PublicUrl(thumbKey),
                ["duration_seconds"] = Math.Round(outputDuration, 3),
                ["exported_at"] = DateTimeOffset.UtcNow.ToString("o"),
            }, ct);
            job.Append("api", $"export recorded on clip {clip.Id}: {storage.PublicUrl(videoKey)}");
            return 0;
        }
        finally
        {
            CleanUp(work);
        }
    }

    // ---- long-form -------------------------------------------------------- //

    private async Task<int> RunLongformExportAsync(
        PipelineJob job, Guid projectId, JsonObject editRow, EditorDoc doc, CancellationToken ct)
    {
        var work = WorkDir(job.Id);
        try
        {
            var sourceVersion = (int)(editRow["version"]?.GetValue<double>() ?? 1);
            var render = (editRow["metadata"] as JsonObject)?["render"] as JsonObject;
            var sourceUrl = editRow["video_url"]?.GetValue<string>()
                ?? throw new InvalidOperationException("the long-form cut has no video to edit");

            // Prefer the local mirror file when this machine ran the pipeline.
            var localName = render?["filename"]?.GetValue<string>();
            var localPath = localName is null
                ? null
                : Path.Combine(layout.ProjectDir(projectId), "longform", localName);
            var source = localPath is not null && File.Exists(localPath) ? localPath : sourceUrl;

            var (outputPath, thumbPath, outputDuration) =
                await RenderAsync(job, work, source, doc, ct);

            var latest = await db.GetLongformEditAsync(projectId, version: null, ct);
            var newVersion = (int)((latest?["version"]?.GetValue<double>() ?? 0) + 1);
            var fileName = $"longform_v{newVersion}.mp4";
            var videoKey = $"{projectId}/editor/{fileName}";
            var thumbKey = $"{projectId}/editor/longform_v{newVersion}.jpg";
            job.Append("api", $"uploading {videoKey}");
            await storage.UploadFileAsync(videoKey, outputPath, "video/mp4", ct);
            await storage.UploadFileAsync(thumbKey, thumbPath, "image/jpeg", ct);

            var segments = new JsonArray(doc.Segments
                .Select(segment => (JsonNode)new JsonObject
                {
                    ["start_seconds"] = Math.Round(segment.SrcStart, 3),
                    ["end_seconds"] = Math.Round(segment.SrcEnd, 3),
                })
                .ToArray());
            var inserted = await db.InsertLongformEditAsync(new JsonObject
            {
                ["project_id"] = projectId.ToString(),
                ["version"] = newVersion,
                ["status"] = "rendered",
                ["video_url"] = storage.PublicUrl(videoKey),
                ["thumbnail_url"] = storage.PublicUrl(thumbKey),
                ["duration_seconds"] = Math.Round(outputDuration, 3),
                ["segments"] = segments,
                ["revision"] = new JsonObject
                {
                    ["request"] = "Manual edit in the studio editor",
                    ["notes"] = $"cut from v{sourceVersion} on the editor timeline "
                        + $"({doc.Segments.Count} segment(s))",
                },
                ["metadata"] = new JsonObject
                {
                    // The longform_edits delete trigger reads metadata.render —
                    // these objects get cleaned up with the row.
                    ["render"] = new JsonObject
                    {
                        ["bucket"] = storage.Bucket,
                        ["storage_path"] = videoKey,
                        ["thumbnail_storage_path"] = thumbKey,
                        ["filename"] = fileName,
                        ["title"] = render?["title"]?.DeepClone(),
                        ["thumbnails"] = render?["thumbnails"]?.DeepClone(),
                    },
                    ["manual_edit"] = new JsonObject
                    {
                        ["source_version"] = sourceVersion,
                        ["edl"] = EditorDocs.ToJson(doc),
                    },
                },
            }, ct);
            job.Append("api", $"long-form v{newVersion} recorded "
                + $"({storage.PublicUrl(videoKey)})");

            // Best-effort mirror updates so worker verbs (thumbnails, publish)
            // can see the new version on this machine.
            TryMirrorNewVersion(job, projectId, inserted, outputPath, fileName);
            return 0;
        }
        finally
        {
            CleanUp(work);
        }
    }

    // ---- shared ------------------------------------------------------------ //

    private async Task<(string OutputPath, string ThumbPath, double OutputDuration)> RenderAsync(
        PipelineJob job, string work, string source, EditorDoc doc, CancellationToken ct)
    {
        void Line(string text) => job.Append("out", text);

        var sourcePath = source;
        if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            sourcePath = Path.Combine(work, "source.mp4");
            job.Append("api", $"downloading source: {source}");
            using var client = httpFactory.CreateClient(HttpClientName);
            await using var remote = await client.GetStreamAsync(source, ct);
            await using var local = File.Create(sourcePath);
            await remote.CopyToAsync(local, ct);
        }
        else
        {
            job.Append("api", $"using local source: {source}");
        }

        var info = await renderer.ProbeAsync(sourcePath, Line, ct);
        job.Append("api", $"source: {info.Width}x{info.Height}, {info.Duration:0.##}s, "
            + $"audio={(info.HasAudio ? "yes" : "no")}");
        if (EditorDocs.Validate(doc, info.Duration) is { } invalid)
            throw new InvalidOperationException($"document does not fit the source: {invalid}");

        // This machine's ffmpeg has no libass/drawtext: captions and text
        // overlays are rasterized to PNGs (ImageSharp) and composited with the
        // overlay filter. ffmpeg runs inside the work dir so every input in the
        // graph is a bare relative filename.
        var plan = EditorRenderer.PlanOverlays(doc);
        foreach (var item in plan)
        {
            var png = item.Kind == "text"
                ? CaptionRasterizer.RenderTextOverlay(item.Text, info.Width, info.Height)
                : CaptionRasterizer.RenderCaption(item.Text, doc.CaptionStyle,
                    info.Width, info.Height, item.Words, item.HighlightWords);
            await File.WriteAllBytesAsync(Path.Combine(work, item.FileName), png, ct);
        }
        if (plan.Count > 0)
            job.Append("api", $"captions/text: {plan.Count} overlay(s), style {doc.CaptionStyle}");
        var overlays = plan
            .Select(item => new EditorRenderer.OverlaySpec(item.FileName, item.Start, item.End, item.YExpr))
            .ToList();

        string? padPath = null;
        if (doc.Audio.Music > 0.001)
        {
            padPath = "pad.wav";
            job.Append("api", "synthesizing music bed");
            await renderer.RunAsync("ffmpeg", EditorRenderer.BuildPadArgs(padPath), Line, ct, work);
        }

        var outputPath = Path.Combine(work, "export.mp4");
        job.Append("api", $"rendering {doc.Segments.Count} segment(s) "
            + $"-> {EditorDocs.OutputDuration(doc):0.##}s");
        await renderer.RunAsync("ffmpeg",
            EditorRenderer.BuildRenderArgs(doc, info, sourcePath, padPath, overlays, outputPath),
            Line, ct, work);

        var outputDuration = EditorDocs.OutputDuration(doc);
        var thumbPath = Path.Combine(work, "thumb.jpg");
        await renderer.RunAsync("ffmpeg",
            EditorRenderer.BuildThumbnailArgs(outputPath, outputDuration * 0.25, thumbPath),
            Line, ct, work);

        var cap = MaxUploadBytes();
        if (new FileInfo(outputPath).Length > cap && outputDuration > 0)
        {
            // 92% of the cap's bit budget minus the audio track's share.
            var videoBitrate = Math.Max(
                300_000L, (long)(cap * 8 / outputDuration * 0.92) - 160_000L);
            var fitPath = Path.Combine(work, "export_fit.mp4");
            job.Append("api", $"export exceeds the storage cap; re-encoding at "
                + $"{videoBitrate / 1000} kbps to fit");
            await renderer.RunAsync("ffmpeg",
                EditorRenderer.BuildFitArgs(outputPath, fitPath, videoBitrate), Line, ct, work);
            outputPath = fitPath;
        }
        return (outputPath, thumbPath, outputDuration);
    }

    private void TryMirrorNewVersion(
        PipelineJob job, Guid projectId, JsonObject insertedRow, string outputPath, string fileName)
    {
        try
        {
            var projectDir = layout.ProjectDir(projectId);
            if (!Directory.Exists(projectDir)) return;
            var longformDir = Path.Combine(projectDir, "longform");
            Directory.CreateDirectory(longformDir);
            File.Copy(outputPath, Path.Combine(longformDir, fileName), overwrite: true);
            File.AppendAllText(Path.Combine(projectDir, "longform_edits.jsonl"),
                insertedRow.ToJsonString() + "\n");
            job.Append("api", "mirror updated with the new version");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            job.Append("api", $"mirror update skipped: {exception.Message}");
            log.LogWarning("Mirror update for project {ProjectId} skipped: {Message}",
                projectId, exception.Message);
        }
    }

    private string WorkDir(string jobId)
    {
        var path = Path.Combine(layout.OutputsRoot, "api", "editor", jobId);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanUp(string work)
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
