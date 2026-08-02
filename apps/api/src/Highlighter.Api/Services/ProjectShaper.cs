using System.Globalization;
using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;

namespace Highlighter.Api.Services;

/// <summary>Pure snake_case PostgREST row → DTO mapping, including the derived
/// fields (progress, duration, embedded counts). No I/O.</summary>
public static class ProjectShaper
{
    public static ProjectSummaryDto Summary(JsonObject row, string? activeJobId = null)
    {
        var status = Str(row["status"]) ?? "created";
        var metadata = row["metadata"] as JsonObject;
        var ingest = metadata?["ingest"] as JsonObject;
        var apiRequest = (metadata?["api"] as JsonObject)?["request"] as JsonObject;

        var chunkCount = EmbedCount(row["transcript_chunks"]);
        var sourceMinutes = Dbl(ingest?["source_minutes"]);
        var chunkSeconds = Dbl(ingest?["chunk_seconds"]) ?? Dbl(apiRequest?["chunk_seconds"]) ?? 90;
        var maxChunks = IntOrNull(apiRequest?["max_chunks"]) ?? IntOrNull(ingest?["max_chunks"]) ?? 0;

        return new ProjectSummaryDto(
            Id: Guid.Parse(Str(row["id"])!),
            Name: Str(row["name"]) ?? "",
            SourceType: Str(row["source_type"]) ?? "",
            SourceUrl: Str(row["source_url"]) ?? "",
            Status: status,
            Pipeline: Str(apiRequest?["pipeline"]) ?? Str(ingest?["pipeline"]) ?? "short",
            Error: Str(row["error"]),
            MinClipScore: Dbl(row["min_clip_score"]),
            DurationSeconds: sourceMinutes * 60,
            ClipCount: EmbedCount(row["clips"]),
            ChunkCount: chunkCount,
            LongformCount: EmbedCount(row["longform_edits"]),
            Processing: status is "created" or "ingesting" or "stopping",
            Progress: BuildProgress(status, chunkCount, sourceMinutes, chunkSeconds, maxChunks,
                Str((metadata?["progress"] as JsonObject)?["stage"])),
            ActiveJobId: activeJobId,
            CreatedAt: Ts(row["created_at"]),
            UpdatedAt: Ts(row["updated_at"]),
            ThumbnailUrl: SummaryThumbnail(row));
    }

    /// <summary>Card cover: the newest long-form thumbnail, else the best clip
    /// frame. Works off both list rows (aliased thumbs/clip_thumbs embeds) and
    /// detail rows (full longform_edits/clips arrays).</summary>
    private static string? SummaryThumbnail(JsonObject row)
    {
        foreach (var edit in Rows(row["thumbs"]).Concat(Rows(row["longform_edits"])))
            if (Str(edit["thumbnail_url"]) is { } editThumb) return editThumb;
        foreach (var clip in Rows(row["clip_thumbs"]).Concat(Rows(row["clips"])))
        {
            var metadata = clip["metadata"] as JsonObject;
            var url = Str(metadata?["thumbnail_url"])
                ?? Str((metadata?["render"] as JsonObject)?["thumbnail_url"]);
            if (url is not null) return url;
        }
        return null;
    }

    public static ProjectDetailDto Detail(JsonObject row, bool hasLocalMirror, string? activeJobId = null) =>
        new(
            Project: Summary(row, activeJobId),
            HasLocalMirror: hasLocalMirror,
            Clips: Rows(row["clips"]).Select(Clip).ToList(),
            LongformEdits: Rows(row["longform_edits"]).Select(Longform).ToList(),
            Publications: Rows(row["publications"]).Select(Publication).ToList());

    /// <summary>Where each finishing phase sits on the bar. Capture (transcript
    /// chunks) is the only phase with a real fraction, so it owns the first
    /// CaptureShare; everything after it is a single long step that gets a fixed
    /// landmark. Without this the bar reached 100% the moment the last chunk
    /// stored and sat there through the edit LLM call, the stitch, and four
    /// image generations.</summary>
    private const double CaptureShare = 0.72;

    private static readonly Dictionary<string, double> TailStages = new()
    {
        ["finishing"] = 0.75,
        ["editing"] = 0.80,
        ["stitching"] = 0.86,
        ["thumbnails"] = 0.90,
        ["uploading"] = 0.96,
    };

    public static ProgressDto BuildProgress(
        string status, int chunksStored, double? sourceMinutes, double chunkSeconds, int maxChunks,
        string? workerStage = null)
    {
        var stage = status == "created" ? "queued" : status;
        int? expected = null;
        if (sourceMinutes is > 0 && chunkSeconds > 0)
        {
            expected = (int)Math.Ceiling(sourceMinutes.Value * 60 / chunkSeconds);
            if (maxChunks > 0) expected = Math.Min(expected.Value, maxChunks);
        }
        else if (maxChunks > 0)
        {
            expected = maxChunks;
        }

        if (status == "ready") return new ProgressDto(stage, 1.0, chunksStored, expected);
        if (status != "ingesting") return new ProgressDto(stage, null, chunksStored, expected);

        // A worker that predates the stage markers reports nothing here; the
        // capture fraction alone is exactly the old behavior.
        if (workerStage is { } marker && TailStages.TryGetValue(marker, out var landmark))
            return new ProgressDto(marker, landmark, chunksStored, expected);

        var captured = expected is > 0
            ? Math.Min(1.0, chunksStored / (double)expected.Value) * CaptureShare
            : (double?)null;
        return new ProgressDto(workerStage ?? stage, captured, chunksStored, expected);
    }

    public static ClipDto Clip(JsonObject row)
    {
        var metadata = row["metadata"] as JsonObject;
        var render = metadata?["render"] as JsonObject;
        var start = Dbl(row["start_seconds"]) ?? 0;
        var end = Dbl(row["end_seconds"]) ?? 0;
        return new ClipDto(
            Id: Guid.Parse(Str(row["id"])!),
            Title: Str(row["title"]) ?? "",
            Description: Str(row["description"]),
            StartSeconds: start,
            EndSeconds: end,
            DurationSeconds: Math.Round(end - start, 3),
            Score: Dbl(row["score"]),
            Status: Str(row["status"]) ?? "detected",
            Pipeline: Str(metadata?["pipeline"]) ?? "short",
            VideoUrl: Str(row["video_url"]),
            VerticalUrl: Str(row["vertical_url"]),
            CaptionedUrl: Str(row["captioned_url"]),
            ThumbnailUrl: Str(metadata?["thumbnail_url"]) ?? Str(render?["thumbnail_url"]),
            FileName: Str(render?["filename"]),
            CreatedAt: Ts(row["created_at"]));
    }

    public static LongformEditDto Longform(JsonObject row)
    {
        var render = (row["metadata"] as JsonObject)?["render"] as JsonObject;
        var thumbnails = render?["thumbnails"] as JsonObject;
        var variants = Rows(thumbnails?["variants"])
            .Select(variant => new ThumbnailVariantDto(
                IntOrNull(variant["index"]) ?? 0,
                Str(variant["direction"]),
                Str(variant["overlay_text"]),
                Str(variant["url"]),
                Str(variant["error"])))
            .ToList();
        // selected_index is now the variant's stable number. Rows written before
        // that change hold a LIST position; indices start at 1, so a 0 (or any
        // value that matches no variant) is read the old way.
        var raw = IntOrNull(thumbnails?["selected_index"]);
        int? selected = raw switch
        {
            { } value when variants.Any(v => v.Index == value) => value,
            { } position when position >= 0 && position < variants.Count => variants[position].Index,
            _ => null,
        };

        return new LongformEditDto(
            Id: Guid.Parse(Str(row["id"])!),
            Version: IntOrNull(row["version"]) ?? 1,
            Status: Str(row["status"]) ?? "rendered",
            VideoUrl: Str(row["video_url"]),
            ThumbnailUrl: Str(row["thumbnail_url"]),
            DurationSeconds: Dbl(row["duration_seconds"]),
            Segments: Rows(row["segments"])
                .Select(segment => new LongformSegmentDto(
                    IntOrNull(segment["chunk_index"]),
                    Str(segment["title"]),
                    Dbl(segment["start_seconds"]) ?? 0,
                    Dbl(segment["end_seconds"]) ?? 0))
                .ToList(),
            RevisionRequest: Str((row["revision"] as JsonObject)?["request"]),
            CreatedAt: Ts(row["created_at"]),
            Title: Str(render?["title"]),
            Thumbnails: variants,
            SelectedThumbnail: selected);
    }

    public static PublicationDto Publication(JsonObject row)
    {
        var metadata = row["metadata"] as JsonObject;
        return new PublicationDto(
            Id: Guid.Parse(Str(row["id"])!),
            Target: Str(row["target"]) ?? "",
            Platform: Str(row["platform"]) ?? "",
            Url: Str(row["url"]),
            Title: Str(row["title"]),
            FileName: Str(metadata?["filename"]),
            LongformVersion: IntOrNull(metadata?["longform_version"]),
            CreatedAt: Ts(row["created_at"]));
    }

    public static TranscriptChunkDto TranscriptChunk(JsonObject row, bool includeWords) =>
        new(
            ChunkIndex: IntOrNull(row["chunk_index"]) ?? 0,
            StartSeconds: Dbl(row["start_seconds"]) ?? 0,
            EndSeconds: Dbl(row["end_seconds"]) ?? 0,
            Transcript: Str(row["transcript"]) ?? "",
            Words: includeWords
                ? Rows(row["words"])
                    .Select(word => new WordDto(
                        Str(word["word"]) ?? "",
                        Str(word["punctuated_word"]),
                        Dbl(word["start"]) ?? 0,
                        Dbl(word["end"]) ?? 0,
                        Dbl(word["absolute_start"]),
                        Dbl(word["absolute_end"])))
                    .ToList()
                : null);

    /// <summary>An embedded relation is either a count aggregate ([{"count": n}])
    /// or the full row array — handle both so list and detail share one shaper.</summary>
    public static int EmbedCount(JsonNode? node)
    {
        if (node is not JsonArray array) return 0;
        if (array is [JsonObject only] && only.Count == 1 && only.ContainsKey("count"))
            return IntOrNull(only["count"]) ?? 0;
        return array.Count;
    }

    private static IEnumerable<JsonObject> Rows(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>() : [];

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Dbl(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var asDouble)) return asDouble;
        if (value.TryGetValue<decimal>(out var asDecimal)) return (double)asDecimal;
        if (value.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static int? IntOrNull(JsonNode? node) =>
        Dbl(node) is { } value ? (int)value : null;

    private static DateTimeOffset Ts(JsonNode? node) =>
        Str(node) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : default;
}
