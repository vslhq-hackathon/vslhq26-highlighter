using System.Text.Json;
using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;

namespace Highlighter.Api.Services;

/// <summary>Pure EDL helpers: defaults, transcript-word caption seeding,
/// validation, source→output time mapping, and (de)serialization for the
/// metadata jsonb columns the documents persist in.</summary>
public static class EditorDocs
{
    public const int Version = 1;
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static readonly string[] CaptionStyles = ["boxed", "plain", "karaoke"];

    /// <summary>"source" keeps the media's own geometry (what every document
    /// did before formats existed); the rest are fixed delivery sizes.</summary>
    public static readonly string[] Formats = ["source", "vertical", "square", "wide"];

    /// <summary>cutStart/cutEnd place the seeded segment inside a padded
    /// editing master (handle material outside the cut stays off the timeline
    /// until the user extends into it); the defaults cover the whole source.</summary>
    public static EditorDoc Default(double sourceDuration, IReadOnlyList<EdlCaption> captions,
        double cutStart = 0, double? cutEnd = null, string format = "source")
    {
        var start = Math.Max(0, cutStart);
        var end = Math.Max(start + 0.04, cutEnd ?? Math.Max(0.04, sourceDuration));
        return new(Version,
            Segments: [new EdlSegment("s1", start, end)],
            Captions: captions,
            CaptionStyle: "boxed",
            Texts: [],
            Markers: [],
            Transform: new EdlTransform(),
            Audio: new EdlAudio(),
            Reframe: "auto",
            Format: format,
            CaptionsEnabled: true);
    }

    /// <summary>Shift every timing in the document by a constant offset — used
    /// to re-base a doc authored against the bare clip onto its padded master.</summary>
    public static EditorDoc ShiftDoc(EditorDoc doc, double offset) => doc with
    {
        Segments = doc.Segments
            .Select(s => s with { SrcStart = s.SrcStart + offset, SrcEnd = s.SrcEnd + offset })
            .ToList(),
        Captions = doc.Captions
            .Select(c => c with
            {
                Start = c.Start + offset,
                End = c.End + offset,
                Words = c.Words?.Select(w => w with { S = w.S + offset, E = w.E + offset }).ToList(),
            })
            .ToList(),
        Texts = doc.Texts
            .Select(t => t with { Start = t.Start + offset, End = t.End + offset })
            .ToList(),
        Markers = doc.Markers.Select(m => m with { At = m.At + offset }).ToList(),
    };

    /// <summary>Caption lines from transcript words whose ABSOLUTE timings fall
    /// inside [windowStart, windowEnd] on the source clock; emitted times are
    /// re-based to the edited file (t=0 at windowStart). Lines break on speech
    /// gaps, word count, or line duration — matching short-form caption pacing.</summary>
    public static List<EdlCaption> SeedCaptions(
        IEnumerable<JsonObject> transcriptChunks, double windowStart, double windowEnd)
    {
        var words = new List<(string Text, double Start, double End)>();
        foreach (var chunk in transcriptChunks)
        {
            foreach (var node in chunk["words"] as JsonArray ?? [])
            {
                if (node is not JsonObject word) continue;
                var start = Dbl(word["absolute_start"]) ?? Dbl(word["start"]);
                var end = Dbl(word["absolute_end"]) ?? Dbl(word["end"]);
                var text = word["punctuated_word"]?.GetValue<string>()
                    ?? word["word"]?.GetValue<string>();
                if (start is null || end is null || string.IsNullOrWhiteSpace(text)) continue;
                if (end <= windowStart || start >= windowEnd) continue;
                words.Add((text!, Math.Max(start.Value, windowStart), Math.Min(end.Value, windowEnd)));
            }
        }
        words.Sort((a, b) => a.Start.CompareTo(b.Start));

        var captions = new List<EdlCaption>();
        var line = new List<(string Text, double Start, double End)>();
        void Flush()
        {
            if (line.Count == 0) return;
            var index = captions.Count + 1;
            captions.Add(new EdlCaption(
                $"c{index}",
                Math.Round(line[0].Start - windowStart, 3),
                Math.Round(line[^1].End - windowStart, 3),
                string.Join(' ', line.Select(w => w.Text)),
                line.Select(w => new EdlWord(w.Text,
                    Math.Round(w.Start - windowStart, 3),
                    Math.Round(w.End - windowStart, 3))).ToList()));
            line.Clear();
        }

        foreach (var word in words)
        {
            if (line.Count > 0)
            {
                var gap = word.Start - line[^1].End;
                var span = word.End - line[0].Start;
                if (gap > 0.6 || line.Count >= 6 || span > 3.5) Flush();
            }
            line.Add(word);
        }
        Flush();
        return captions;
    }

    /// <summary>Move a seeded caption list onto a later stretch of the timeline
    /// and renumber its ids. SeedCaptions re-bases every window to t=0 and
    /// restarts ids at c1, so stitching several windows together needs both.</summary>
    public static List<EdlCaption> OffsetCaptions(
        IEnumerable<EdlCaption> captions, double offset, int startIndex)
    {
        var shifted = new List<EdlCaption>();
        var index = startIndex;
        foreach (var caption in captions)
        {
            shifted.Add(caption with
            {
                Id = $"c{index++}",
                Start = Math.Round(caption.Start + offset, 3),
                End = Math.Round(caption.End + offset, 3),
                Words = caption.Words?.Select(word => word with
                {
                    S = Math.Round(word.S + offset, 3),
                    E = Math.Round(word.E + offset, 3),
                }).ToList(),
            });
        }
        return shifted;
    }

    /// <summary>Fill in every optional/absent piece of a deserialized document —
    /// a JSON body (or an older persisted doc) can legally omit whole sections,
    /// and positional records deserialize those to null.</summary>
    public static EditorDoc Normalize(EditorDoc doc) => doc with
    {
        Segments = doc.Segments ?? [],
        Captions = doc.Captions ?? [],
        CaptionStyle = string.IsNullOrEmpty(doc.CaptionStyle) ? "boxed" : doc.CaptionStyle,
        Texts = doc.Texts ?? [],
        Markers = doc.Markers ?? [],
        Transform = doc.Transform ?? new EdlTransform(),
        Audio = doc.Audio ?? new EdlAudio(),
        Reframe = string.IsNullOrEmpty(doc.Reframe) ? "auto" : doc.Reframe,
        // "source" is the pre-format behavior, so old documents keep exporting
        // at their media's own geometry.
        Format = string.IsNullOrEmpty(doc.Format) ? "source" : doc.Format,
        CaptionsEnabled = doc.CaptionsEnabled ?? true,
    };

    /// <summary>Null when valid, else a caller-facing error message. Collection
    /// caps keep a hand-crafted document from turning the export into a
    /// thousands-of-inputs ffmpeg invocation.</summary>
    public static string? Validate(EditorDoc doc, double sourceDuration)
    {
        doc = Normalize(doc);
        if (doc.Segments.Count == 0)
            return "the timeline needs at least one segment";
        if (doc.Segments.Count > 200)
            return "too many segments (max 200)";
        var previousEnd = -1e-3;
        foreach (var segment in doc.Segments)
        {
            if (segment.SrcStart < -1e-3 || segment.SrcEnd <= segment.SrcStart)
                return $"segment {segment.Id}: start must be >= 0 and before end";
            if (sourceDuration > 0 && segment.SrcEnd > sourceDuration + 0.5)
                return $"segment {segment.Id}: ends past the end of the source";
            if (segment.SrcStart < previousEnd - 1e-3)
                return $"segment {segment.Id}: segments must be chronological and non-overlapping";
            if (segment.Speed is < 0.5 or > 2.0)
                return $"segment {segment.Id}: speed must be between 0.5 and 2";
            previousEnd = segment.SrcEnd;
        }
        if (!CaptionStyles.Contains(doc.CaptionStyle))
            return "captionStyle must be boxed, plain or karaoke";
        if (doc.Reframe is not ("auto" or "manual"))
            return "reframe must be auto or manual";
        if (doc.Format is not { } format || !Formats.Contains(format))
            return "format must be source, vertical, square or wide";
        if (doc.Transform.Scale is < 1.0 or > 3.0) return "transform.scale must be 1..3";
        if (doc.Transform.PosX is < -1.0 or > 1.0) return "transform.posX must be -1..1";
        if (doc.Audio.Voice is < 0 or > 2) return "audio.voice must be 0..2";
        if (doc.Audio.Music is < 0 or > 1) return "audio.music must be 0..1";
        if (doc.Captions.Count > 500) return "too many captions (max 500)";
        foreach (var caption in doc.Captions)
        {
            if (caption.End <= caption.Start)
                return $"caption {caption.Id}: end must be after start";
            if (caption.Text?.Length > 500)
                return $"caption {caption.Id}: text is too long (max 500 characters)";
            if (caption.Words?.Count > 40)
                return $"caption {caption.Id}: too many words (max 40)";
        }
        if (doc.Texts.Count > 50) return "too many text overlays (max 50)";
        foreach (var text in doc.Texts)
        {
            if (text.End <= text.Start) return $"text {text.Id}: end must be after start";
            if (text.Y is < 0 or > 1) return $"text {text.Id}: y must be 0..1";
            if (text.Text?.Length > 300)
                return $"text {text.Id}: text is too long (max 300 characters)";
        }
        return null;
    }

    public static double OutputDuration(EditorDoc doc) =>
        doc.Segments.Sum(segment => (segment.SrcEnd - segment.SrcStart) / segment.Speed);

    /// <summary>Map a source time into output time; null when it falls in a cut
    /// region. Speed within a segment compresses/stretches proportionally.</summary>
    public static double? SourceToOutput(EditorDoc doc, double sourceTime)
    {
        double elapsed = 0;
        foreach (var segment in doc.Segments)
        {
            if (sourceTime < segment.SrcStart - 1e-6)
                return null;
            if (sourceTime <= segment.SrcEnd + 1e-6)
                return elapsed + Math.Max(0, sourceTime - segment.SrcStart) / segment.Speed;
            elapsed += (segment.SrcEnd - segment.SrcStart) / segment.Speed;
        }
        return null;
    }

    /// <summary>Map a source window into the output timeline, clipped to the
    /// visible parts. A window spanning a cut yields its visible extent.</summary>
    public static (double Start, double End)? MapWindow(EditorDoc doc, double start, double end)
    {
        double elapsed = 0;
        double? outStart = null, outEnd = null;
        foreach (var segment in doc.Segments)
        {
            var visibleStart = Math.Max(start, segment.SrcStart);
            var visibleEnd = Math.Min(end, segment.SrcEnd);
            if (visibleEnd > visibleStart)
            {
                var mappedStart = elapsed + (visibleStart - segment.SrcStart) / segment.Speed;
                var mappedEnd = elapsed + (visibleEnd - segment.SrcStart) / segment.Speed;
                outStart ??= mappedStart;
                outEnd = mappedEnd;
            }
            elapsed += (segment.SrcEnd - segment.SrcStart) / segment.Speed;
        }
        return outStart is { } s && outEnd is { } e && e > s ? (s, e) : null;
    }

    public static JsonNode ToJson(EditorDoc doc) => JsonSerializer.SerializeToNode(doc, Json)!;

    public static EditorDoc? FromJson(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            // Normalized on the way in: a doc persisted by an older writer may
            // omit sections that deserialize to null.
            return node.Deserialize<EditorDoc>(Json) is { } doc ? Normalize(doc) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? Dbl(JsonNode? node)
    {
        try
        {
            return node?.GetValue<double>();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
