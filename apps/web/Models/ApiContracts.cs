// Mirrors apps/api/src/Highlighter.Api/Contracts — the API is the source of
// truth. Deliberately duplicated (no ProjectReference): the web build stays
// decoupled from the API tree, and System.Net.Http.Json's Web defaults bind
// these positional records straight from the API's camelCase JSON.
namespace Highlighter.Web.Models;

public record ProgressDto(string Stage, double? Percent, int ChunksStored, int? ChunksExpected);

public record ProjectSummaryDto(
    Guid Id,
    string Name,
    string SourceType,
    string SourceUrl,
    string Status,
    string Pipeline,
    string? Error,
    double? MinClipScore,
    double? DurationSeconds,
    int ClipCount,
    int ChunkCount,
    int LongformCount,
    bool Processing,
    ProgressDto Progress,
    string? ActiveJobId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ThumbnailUrl = null);

public record ProjectDetailDto(
    ProjectSummaryDto Project,
    bool HasLocalMirror,
    IReadOnlyList<ClipDto> Clips,
    IReadOnlyList<LongformEditDto> LongformEdits,
    IReadOnlyList<PublicationDto> Publications);

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

public record ThumbnailVariantDto(int Index, string? Direction, string? OverlayText, string? Url);

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

public record WordDto(
    string Word,
    string? PunctuatedWord,
    double Start,
    double End,
    double? AbsoluteStart,
    double? AbsoluteEnd);

public record CreateProjectRequest(
    string? SourceUrl,
    string? Pipeline,
    string? Name = null,
    string? Instructions = null,
    string? TargetMinutes = null,
    double? MinClipScore = null,
    int MaxChunks = 0,
    int ChunkSeconds = 90);

public record CancelResultDto(Guid ProjectId, string Status, string? JobId, bool ForceKilled);

public record ReviseRequestDto(string? Request, bool FromChat = false);

/// <summary>One durable studio-agent chat message. JobFinal marks the
/// server-written completion message for the job JobId started.</summary>
public record AgentMessageDto(
    Guid Id,
    string Role,
    string Text,
    string? JobId,
    bool JobFinal,
    DateTimeOffset CreatedAt);

public record PublishRequestDto(
    string? Target,
    Guid? ClipId = null,
    int? Version = null,
    string[]? Platforms = null,
    string? Title = null,
    string? Thumbnail = null,
    bool Plain = false,
    bool DryRun = false);

public record JobDto(
    string Id,
    string Kind,
    Guid? ProjectId,
    string State,
    IReadOnlyList<string> Argv,
    int? ExitCode,
    string? FailureReason,
    string? LogPath,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long LastLogSeq);

public record JobLogLineDto(long Seq, DateTimeOffset At, string Stream, string Line);

// ---- editor (EDL) ----

public record EdlSegment(string Id, double SrcStart, double SrcEnd, double Speed = 1.0);

public record EdlWord(string T, double S, double E);

public record EdlCaption(
    string Id, double Start, double End, string Text, IReadOnlyList<EdlWord>? Words = null);

public record EdlText(string Id, double Start, double End, string Text, double Y = 0.4);

public record EdlMarker(string Id, double At, string? Label = null);

public record EdlTransform(double Scale = 1.0, double PosX = 0.0);

public record EdlAudio(double Voice = 1.0, double Music = 0.0);

public record EditorDoc(
    int V,
    IReadOnlyList<EdlSegment> Segments,
    IReadOnlyList<EdlCaption> Captions,
    string CaptionStyle,
    IReadOnlyList<EdlText> Texts,
    IReadOnlyList<EdlMarker> Markers,
    EdlTransform Transform,
    EdlAudio Audio,
    string Reframe);

public record EditorExportInfoDto(
    string? Url, string? ThumbnailUrl, double? DurationSeconds, DateTimeOffset? ExportedAt);

public record EditorHandlesDto(double Lead, double Tail);

public record EditorDocResponse(
    EditorDoc Doc,
    string Target,
    Guid? ClipId,
    int? LongformVersion,
    string? SourceUrl,
    string? PosterUrl,
    double SourceDuration,
    DateTimeOffset? SavedAt,
    EditorExportInfoDto? Export,
    double? SourceFps = null,
    EditorHandlesDto? Handles = null);

public record SaveEditorRequest(EditorDoc Doc);

// ---- auth (POST /api/auth/*) ----

public record SignupRequest(string Email, string Password);

public record LoginRequest(string Email, string Password);

public record RefreshRequest(string RefreshToken);

public record AuthUserDto(Guid Id, string Email);

public record AuthSessionResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    AuthUserDto User);

/// <summary>RFC 7807 problem body — every non-2xx API response carries one.</summary>
public record ProblemDto(string? Title, string? Detail, int? Status);
