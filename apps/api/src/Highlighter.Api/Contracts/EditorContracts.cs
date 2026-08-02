namespace Highlighter.Api.Contracts;

// The edit decision list (EDL) behind the studio's timeline editor. All times
// are SOURCE seconds of the media file being edited (a clip render or a
// long-form cut) — cutting a segment does not shift caption/text/marker
// timings; the renderer maps source→output when burning.

public record EdlSegment(string Id, double SrcStart, double SrcEnd, double Speed = 1.0);

public record EdlWord(string T, double S, double E);

public record EdlCaption(
    string Id, double Start, double End, string Text, IReadOnlyList<EdlWord>? Words = null);

public record EdlText(string Id, double Start, double End, string Text, double Y = 0.4);

public record EdlMarker(string Id, double At, string? Label = null);

/// <summary>Zoom/pan for the manual reframe: Scale 1..3 crops into the frame,
/// PosX −1..1 pans the crop window left/right.</summary>
public record EdlTransform(double Scale = 1.0, double PosX = 0.0);

/// <summary>Track gains: Voice scales the source audio (0..2); Music mixes the
/// generated ambient bed under it (0 = off .. 1).</summary>
public record EdlAudio(double Voice = 1.0, double Music = 0.0);

/// <param name="Format">Delivery aspect: "vertical" (1080×1920), "square"
/// (1080×1080), "wide" (1920×1080), or "source" — keep the media's own
/// geometry. Absent on documents saved before formats existed, which normalize
/// to "source" so their exports stay byte-for-byte what they used to be.</param>
/// <param name="CaptionsEnabled">Burn captions on export (and show them in the
/// preview). Absent means enabled.</param>
public record EditorDoc(
    int V,
    IReadOnlyList<EdlSegment> Segments,
    IReadOnlyList<EdlCaption> Captions,
    string CaptionStyle,
    IReadOnlyList<EdlText> Texts,
    IReadOnlyList<EdlMarker> Markers,
    EdlTransform Transform,
    EdlAudio Audio,
    string Reframe,
    string? Format = null,
    bool? CaptionsEnabled = null);

/// <summary>The last export of this document, if any.</summary>
public record EditorExportInfoDto(
    string? Url, string? ThumbnailUrl, double? DurationSeconds, DateTimeOffset? ExportedAt);

/// <summary>Handle material on a clip's padded editing master: Lead/Tail are
/// the seconds of extra footage rendered before/after the original cut, so
/// the editor can trim outward past the LLM's cut points.</summary>
public record EditorHandlesDto(double Lead, double Tail);

/// <summary>GET .../editor — the document plus everything the editor UI needs
/// about the media it edits.</summary>
public record EditorDocResponse(
    EditorDoc Doc,
    string Target,             // "clip" | "longform"
    Guid? ClipId,
    int? LongformVersion,
    string? SourceUrl,         // the clean media the editor previews
    string? PosterUrl,
    double SourceDuration,
    DateTimeOffset? SavedAt,
    EditorExportInfoDto? Export,
    double? SourceFps = null,
    EditorHandlesDto? Handles = null);

public record SaveEditorRequest(EditorDoc? Doc);

/// <summary>POST .../editor/export — Doc optional (saves first when present).</summary>
public record ExportEditorRequest(EditorDoc? Doc = null);
