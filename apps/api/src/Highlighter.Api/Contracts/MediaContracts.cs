namespace Highlighter.Api.Contracts;

public record ClipDto(
    Guid Id,
    string Title,
    string? Description,
    double StartSeconds,
    double EndSeconds,
    double DurationSeconds,
    double? Score,
    string Status,
    string Pipeline,
    string? VideoUrl,
    string? VerticalUrl,
    string? CaptionedUrl,
    string? ThumbnailUrl,
    string? FileName,
    DateTimeOffset CreatedAt);

public record LongformEditDto(
    Guid Id,
    int Version,
    string Status,
    string? VideoUrl,
    string? ThumbnailUrl,
    double? DurationSeconds,
    IReadOnlyList<LongformSegmentDto> Segments,
    string? RevisionRequest,
    DateTimeOffset CreatedAt,
    string? Title = null,
    IReadOnlyList<ThumbnailVariantDto>? Thumbnails = null,
    int? SelectedThumbnail = null);

/// <summary>A generated long-form thumbnail concept (metadata.render.thumbnails).
/// Index is the variant's stable number; SelectedThumbnail on the edit points at
/// the Index currently set as the video's thumbnail. Error is set (and Url null)
/// when the image model failed for that concept — the slot is kept so the studio
/// can show and retry it instead of silently skipping a number.</summary>
public record ThumbnailVariantDto(
    int Index, string? Direction, string? OverlayText, string? Url, string? Error = null);

public record LongformSegmentDto(
    int? ChunkIndex, string? Title, double StartSeconds, double EndSeconds);

public record PublicationDto(
    Guid Id,
    string Target,
    string Platform,
    string? Url,
    string? Title,
    string? FileName,
    int? LongformVersion,
    DateTimeOffset CreatedAt);

public record TranscriptChunkDto(
    int ChunkIndex,
    double StartSeconds,
    double EndSeconds,
    string Transcript,
    IReadOnlyList<WordDto>? Words);

/// <summary>absolute_start/absolute_end are VOD-clock timings (the pipeline adds
/// them per word) — the caption-friendly values a frontend should prefer.</summary>
public record WordDto(
    string Word,
    string? PunctuatedWord,
    double Start,
    double End,
    double? AbsoluteStart,
    double? AbsoluteEnd);
