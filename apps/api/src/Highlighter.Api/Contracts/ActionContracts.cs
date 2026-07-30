namespace Highlighter.Api.Contracts;

/// <summary>FromChat marks a revision started by the studio agent: the job then
/// writes its completion message into agent_messages when it ends.</summary>
public record ReviseRequestDto(string? Request, bool FromChat = false);

/// <summary>Target "longform" publishes the stitched edit (Version picks one);
/// target "clip" requires ClipId, resolved server-side to the worker's filename
/// handle. Platforms come from the worker's allowed set; "x" is a promo layer and
/// needs a companion platform.</summary>
public record PublishRequestDto(
    string? Target,
    Guid? ClipId = null,
    int? Version = null,
    string[]? Platforms = null,
    string? Title = null,
    string? Thumbnail = null,
    bool Plain = false,
    bool DryRun = false);

public record ReclipRequestDto(
    double StartSeconds, double EndSeconds, string? Title = null, string? Description = null);

/// <summary>POST .../research — rerun web-grounded content research. Mode picks
/// the short- or long-form research context; Focus steers it.</summary>
public record ResearchRequestDto(string? Mode = "long", string? Focus = null);

/// <summary>POST .../thumbnails — generate more long-form thumbnail concepts
/// (latest version unless Version picks one).</summary>
public record ThumbnailsRequestDto(string? Prompt = null, int? Version = null);

/// <summary>POST .../thumbnails/select — make variant Index the video thumbnail.</summary>
public record ThumbnailSelectRequestDto(int Index, int? Version = null);

/// <summary>POST .../thumbnails/import — a user-supplied thumbnail image
/// (base64 payload), stored and selected for the latest (or given) version.</summary>
public record ThumbnailImportRequestDto(string? FileName, string? ContentBase64, int? Version = null);

/// <summary>POST .../clips/{clipId}/reformat — re-render a clip in another
/// delivery format ("square" today), optionally with burned captions.</summary>
public record ReformatRequestDto(string? Format = "square", bool Captions = false);

public record CleanupRequestDto(int Limit = 100);
