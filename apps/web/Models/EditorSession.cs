using System.Text.Json;
using Highlighter.Web.Services;

namespace Highlighter.Web.Models;

/// <summary>The timeline editor's working state: the EDL document, selection,
/// undo/redo, debounced autosave, zoom, playhead and export tracking. Hosted by
/// StudioState (S.Edit) and mutated through Apply(...) so every change lands on
/// the undo stack, marks the doc dirty, and schedules a save. All times in the
/// doc are SOURCE seconds; the timeline DISPLAYS output time (cuts collapsed).</summary>
public sealed class EditorSession(ApiClient api, Action notify)
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private const int MaxUndo = 50;

    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private Timer? _saveTimer;
    private Guid _loadToken;

    public EditorDocResponse? Meta { get; private set; }
    public EditorDoc? Doc { get; private set; }
    public bool Loading { get; private set; }
    public string? Error { get; private set; }
    public string SaveState { get; private set; } = "Saved"; // Saved | Unsaved | Saving… | Save failed
    public string? SelectedId { get; private set; }
    public double PxPerSec { get; private set; } = 12;
    public double Playhead { get; private set; }          // SOURCE seconds (what the <video> reports)
    public double? SeekRequest { get; private set; }      // SOURCE seconds, consumed by the monitor
    public string? PreviewOverrideUrl { get; private set; }

    // Export tracking.
    public bool ExportBusy { get; private set; }
    public string? ExportJobId { get; private set; }
    public string? ExportError { get; private set; }
    public bool ExportDone { get; private set; }
    public string? ExportDoneUrl { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public double OutputDuration => Doc is null ? 0 : Doc.Segments.Sum(s => (s.SrcEnd - s.SrcStart) / s.Speed);
    public double PlayheadOutput => Doc is null ? 0 : OutputAt(Playhead);

    /// <summary>The media's real frame rate (probed server-side), 30 when unknown.</summary>
    public double Fps => Meta?.SourceFps is { } fps && fps > 0 ? fps : 30;
    public double FrameStep => 1.0 / Fps;

    /// <summary>J/K/L shuttle multiplier: 1 = normal, 2/4 = fast forward,
    /// negative = reverse scrub. Shown as a chip in the transport bar.</summary>
    public double ShuttleRate { get; private set; } = 1;

    public void SetShuttleRate(double rate)
    {
        ShuttleRate = rate;
        notify();
    }

    // Handle material (padded editing master): how many seconds the first/last
    // segment can still extend outward into rendered-but-uncut footage.
    public double AvailableLead => Doc?.Segments.FirstOrDefault()?.SrcStart ?? 0;
    public double AvailableTail =>
        Doc is { Segments.Count: > 0 } doc && Meta?.SourceDuration is { } duration && duration > 0
            ? Math.Max(0, duration - doc.Segments[^1].SrcEnd)
            : 0;

    public EdlSegment? SelectedSegment => Doc?.Segments.FirstOrDefault(s => s.Id == SelectedId);
    public EdlCaption? SelectedCaption => Doc?.Captions.FirstOrDefault(c => c.Id == SelectedId);
    public EdlText? SelectedText => Doc?.Texts.FirstOrDefault(t => t.Id == SelectedId);

    // ---- lifecycle ------------------------------------------------------- //

    public async Task LoadAsync(Guid projectId, Guid? clipId, double? fallbackDuration = null)
    {
        var token = Guid.NewGuid();
        _loadToken = token;
        Loading = true;
        Error = null;
        Doc = null;
        Meta = null;
        SelectedId = null;
        PreviewOverrideUrl = null;
        ExportBusy = false;
        ExportJobId = null;
        ExportError = null;
        ExportDone = false;
        ExportDoneUrl = null;
        Playhead = 0;
        _undo.Clear();
        _redo.Clear();
        notify();
        try
        {
            var response = await api.GetEditorDocAsync(projectId, clipId);
            if (_loadToken != token) return;
            Meta = NormalizeMeta(response, fallbackDuration);
            Doc = Meta.Doc;
            SaveState = "Saved";
            FitZoom();
        }
        catch (ApiException exception)
        {
            if (_loadToken != token) return;
            Error = exception.UserMessage;
        }
        catch (Exception)
        {
            if (_loadToken != token) return;
            Error = "Couldn't load the editor — close it and try again.";
        }
        finally
        {
            if (_loadToken == token)
            {
                Loading = false;
                notify();
            }
        }
    }

    /// <summary>Repair holes that would break timeline math: a missing source
    /// duration (falls back to what the project page knows), the degenerate
    /// 0.04s default segment that produces, and out-of-range speeds.</summary>
    private static EditorDocResponse NormalizeMeta(
        EditorDocResponse response, double? fallbackDuration)
    {
        if (response.SourceDuration <= 0 && fallbackDuration is > 0)
            response = response with { SourceDuration = fallbackDuration.Value };
        var doc = response.Doc;
        var segments = (doc.Segments ?? []).Select(s => s with
        {
            Speed = s.Speed is >= 0.5 and <= 2.0 ? s.Speed : 1.0,
        }).ToList();
        if (segments.Count == 1 && segments[0].SrcStart == 0 && segments[0].SrcEnd <= 0.05
            && response.SourceDuration > 0.05)
        {
            // Rebuild inside the original cut, not the whole master — handle
            // material stays outside the timeline until the user extends into it.
            var lead = response.Handles?.Lead ?? 0;
            var tail = response.Handles?.Tail ?? 0;
            segments = [segments[0] with
            {
                SrcStart = lead,
                SrcEnd = Math.Max(lead + 0.1, response.SourceDuration - tail),
            }];
        }
        doc = doc with
        {
            Segments = segments,
            Captions = doc.Captions ?? [],
            Texts = doc.Texts ?? [],
            Markers = doc.Markers ?? [],
            Transform = doc.Transform ?? new EdlTransform(),
            Audio = doc.Audio ?? new EdlAudio(),
        };
        return response with { Doc = doc };
    }

    public void Close()
    {
        _ = FlushSaveAsync();
        _saveTimer?.Dispose();
        _saveTimer = null;
        _loadToken = Guid.NewGuid();
    }

    // ---- time mapping (output-time timeline over a source-time doc) ------- //

    public double? SourceToOutput(double sourceTime)
    {
        if (Doc is null) return null;
        double elapsed = 0;
        foreach (var segment in Doc.Segments)
        {
            if (sourceTime < segment.SrcStart - 1e-6) return null;
            if (sourceTime <= segment.SrcEnd + 1e-6)
                return elapsed + Math.Max(0, sourceTime - segment.SrcStart) / segment.Speed;
            elapsed += (segment.SrcEnd - segment.SrcStart) / segment.Speed;
        }
        return null;
    }

    /// <summary>Total source→output mapping — unlike SourceToOutput it never
    /// returns null: a time inside a cut region maps to the end of the
    /// preceding kept segment, before the first segment to 0, past the last
    /// to the timeline end. Transport math must use this, or a playhead
    /// parked in a cut teleports every step back to zero.</summary>
    public double OutputAt(double sourceTime)
    {
        if (Doc is null) return 0;
        double elapsed = 0;
        foreach (var segment in Doc.Segments)
        {
            if (sourceTime < segment.SrcStart - 1e-6) return elapsed;
            if (sourceTime <= segment.SrcEnd + 1e-6)
                return elapsed + Math.Max(0, sourceTime - segment.SrcStart) / segment.Speed;
            elapsed += (segment.SrcEnd - segment.SrcStart) / segment.Speed;
        }
        return elapsed;
    }

    public double OutputToSource(double outputTime)
    {
        if (Doc is null) return 0;
        double elapsed = 0;
        foreach (var segment in Doc.Segments)
        {
            var span = (segment.SrcEnd - segment.SrcStart) / segment.Speed;
            if (outputTime <= elapsed + span)
                return segment.SrcStart + Math.Max(0, outputTime - elapsed) * segment.Speed;
            elapsed += span;
        }
        return Doc.Segments.Count > 0 ? Doc.Segments[^1].SrcEnd : 0;
    }

    public (double Start, double End)? MapWindow(double start, double end)
    {
        if (Doc is null) return null;
        double elapsed = 0;
        double? outStart = null, outEnd = null;
        foreach (var segment in Doc.Segments)
        {
            var visibleStart = Math.Max(start, segment.SrcStart);
            var visibleEnd = Math.Min(end, segment.SrcEnd);
            if (visibleEnd > visibleStart)
            {
                outStart ??= elapsed + (visibleStart - segment.SrcStart) / segment.Speed;
                outEnd = elapsed + (visibleEnd - segment.SrcStart) / segment.Speed;
            }
            elapsed += (segment.SrcEnd - segment.SrcStart) / segment.Speed;
        }
        return outStart is { } s && outEnd is { } e && e > s ? (s, e) : null;
    }

    /// <summary>Output start of each segment, for ⏮/⏭ and block layout.</summary>
    public List<(EdlSegment Segment, double OutStart, double OutEnd)> SegmentLayout()
    {
        var layout = new List<(EdlSegment, double, double)>();
        if (Doc is null) return layout;
        double elapsed = 0;
        foreach (var segment in Doc.Segments)
        {
            var span = (segment.SrcEnd - segment.SrcStart) / segment.Speed;
            layout.Add((segment, elapsed, elapsed + span));
            elapsed += span;
        }
        return layout;
    }

    // ---- selection / view ------------------------------------------------ //

    public void Select(string? id)
    {
        SelectedId = id;
        notify();
    }

    public void SetPlayhead(double sourceSeconds)
    {
        Playhead = sourceSeconds;
        notify();
    }

    public void RequestSeek(double sourceSeconds)
    {
        SeekRequest = sourceSeconds;
        Playhead = sourceSeconds;
        notify();
    }

    public double? ConsumeSeek()
    {
        var request = SeekRequest;
        SeekRequest = null;
        return request;
    }

    /// <summary>Step the playhead in OUTPUT time (frame keys, ◀ ▶). Stepping in
    /// source time would land inside cut regions, where the playback loop
    /// bounces the playhead away — output time crosses cuts cleanly.</summary>
    public void StepOutput(double deltaSeconds)
    {
        if (Doc is null || OutputDuration <= 0) return;
        var end = Math.Max(0, OutputDuration - Math.Min(FrameStep, 0.02));
        var target = Math.Clamp(PlayheadOutput + deltaSeconds, 0, end);
        RequestSeek(OutputToSource(target));
    }

    /// <summary>⏮/⏭ and ↑/↓: previous/next edit point. Edges are every segment
    /// boundary in output time plus the timeline end. Going back stops at the
    /// current segment's start first (standard NLE behavior); going forward
    /// past the last segment parks at the timeline end — never wraps to 0.</summary>
    public void JumpEdit(int direction)
    {
        if (Doc is null) return;
        var layout = SegmentLayout();
        if (layout.Count == 0) return;
        var edges = layout.Select(entry => entry.OutStart).Append(OutputDuration).ToList();
        var current = PlayheadOutput;
        var tolerance = Math.Max(FrameStep / 2, 0.02);
        if (direction > 0)
        {
            var next = edges.FirstOrDefault(edge => edge > current + tolerance, double.NaN);
            if (double.IsNaN(next)) return; // already parked at the end
            SeekOutputEdge(next, layout);
        }
        else
        {
            SeekOutputEdge(edges.LastOrDefault(edge => edge < current - tolerance, 0), layout);
        }
    }

    private void SeekOutputEdge(
        double outputEdge, List<(EdlSegment Segment, double OutStart, double OutEnd)> layout)
    {
        foreach (var entry in layout)
        {
            if (Math.Abs(entry.OutStart - outputEdge) < 1e-6)
            {
                // A hair inside the segment, so the playback loop can't
                // mistake the boundary for a cut region.
                RequestSeek(entry.Segment.SrcStart + 0.001);
                return;
            }
        }
        RequestSeek(OutputToSource(Math.Max(0, outputEdge - Math.Min(FrameStep, 0.02))));
    }

    public void SetPreview(string? url)
    {
        PreviewOverrideUrl = url;
        notify();
    }

    public void Zoom(double factor) =>
        SetZoom(Math.Clamp(PxPerSec * factor, 2, 400));

    public void FitZoom()
    {
        var duration = Math.Max(1, OutputDuration);
        SetZoom(Math.Clamp(900 / duration, 2, 400));
    }

    private void SetZoom(double pxPerSec)
    {
        PxPerSec = pxPerSec;
        notify();
    }

    /// <summary>Timeline snapping: segment edges and the playhead win inside a
    /// zoom-scaled tolerance (~8px), then the ¼s grid — so drags land exactly
    /// on neighboring cuts the way professional NLEs snap.</summary>
    public double Snap(double seconds, bool snapOn)
    {
        if (!snapOn) return seconds;
        if (Doc is not null)
        {
            var tolerance = 8 / Math.Max(2, PxPerSec);
            double? best = null;
            foreach (var candidate in Doc.Segments
                .SelectMany(s => new[] { s.SrcStart, s.SrcEnd })
                .Append(Playhead))
            {
                var distance = Math.Abs(candidate - seconds);
                if (distance <= tolerance
                    && (best is null || distance < Math.Abs(best.Value - seconds)))
                    best = candidate;
            }
            if (best is { } edge) return edge;
        }
        return Math.Round(seconds * 4) / 4.0;
    }

    // ---- mutations (all undoable) ----------------------------------------- //

    private string? _lastCoalesceKey;
    private DateTimeOffset _lastApplyAt;

    /// <summary>coalesceKey groups rapid slider-style tweaks into ONE undo entry
    /// (the first snapshot of the burst survives; the rest are absorbed).</summary>
    private void Apply(Func<EditorDoc, EditorDoc> mutate, string? coalesceKey = null)
    {
        if (Doc is null) return;
        var now = DateTimeOffset.UtcNow;
        var coalesce = coalesceKey is not null
            && coalesceKey == _lastCoalesceKey
            && now - _lastApplyAt < TimeSpan.FromSeconds(1);
        if (!coalesce)
        {
            _undo.Add(JsonSerializer.Serialize(Doc, Json));
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            _redo.Clear();
        }
        _lastCoalesceKey = coalesceKey;
        _lastApplyAt = now;
        Doc = mutate(Doc);
        MarkDirty();
        notify();
    }

    public void Undo()
    {
        if (Doc is null || _undo.Count == 0) return;
        var restored = JsonSerializer.Deserialize<EditorDoc>(_undo[^1], Json);
        if (restored is null) return;
        _redo.Add(JsonSerializer.Serialize(Doc, Json));
        Doc = restored;
        _undo.RemoveAt(_undo.Count - 1);
        MarkDirty();
        notify();
    }

    public void Redo()
    {
        if (Doc is null || _redo.Count == 0) return;
        var restored = JsonSerializer.Deserialize<EditorDoc>(_redo[^1], Json);
        if (restored is null) return;
        _undo.Add(JsonSerializer.Serialize(Doc, Json));
        Doc = restored;
        _redo.RemoveAt(_redo.Count - 1);
        MarkDirty();
        notify();
    }

    /// <summary>Razor: split the segment containing this SOURCE time.</summary>
    public void SplitAt(double sourceTime) => Apply(doc =>
    {
        var segments = new List<EdlSegment>();
        var count = 0;
        foreach (var segment in doc.Segments)
        {
            if (sourceTime > segment.SrcStart + 0.05 && sourceTime < segment.SrcEnd - 0.05)
            {
                segments.Add(segment with { Id = NewId("s"), SrcEnd = sourceTime });
                segments.Add(segment with { Id = NewId("s"), SrcStart = sourceTime });
                count++;
            }
            else
            {
                segments.Add(segment);
            }
        }
        return count > 0 ? doc with { Segments = segments } : doc;
    });

    public void DeleteSelected()
    {
        if (Doc is null || SelectedId is null) return;
        var id = SelectedId;
        if (Doc.Segments.Count > 1 && Doc.Segments.Any(s => s.Id == id))
            Apply(doc => doc with { Segments = doc.Segments.Where(s => s.Id != id).ToList() });
        else if (Doc.Captions.Any(c => c.Id == id))
            Apply(doc => doc with { Captions = doc.Captions.Where(c => c.Id != id).ToList() });
        else if (Doc.Texts.Any(t => t.Id == id))
            Apply(doc => doc with { Texts = doc.Texts.Where(t => t.Id != id).ToList() });
        else if (Doc.Markers.Any(m => m.Id == id))
            Apply(doc => doc with { Markers = doc.Markers.Where(m => m.Id != id).ToList() });
        SelectedId = null;
    }

    /// <summary>Trim a segment edge (source deltas, clamped by its neighbors).</summary>
    public void TrimSegment(string id, bool leftEdge, double newSourceTime)
    {
        if (Doc is null) return;
        var index = IndexOfSegment(id);
        if (index < 0) return;
        var segment = Doc.Segments[index];
        var min = index > 0 ? Doc.Segments[index - 1].SrcEnd : 0;
        var max = index < Doc.Segments.Count - 1
            ? Doc.Segments[index + 1].SrcStart
            : Math.Max(Meta?.SourceDuration is > 0 and var known ? known : 0, segment.SrcEnd);
        // Math.Clamp throws when min > max (tiny segments, unknown duration) —
        // collapse the range instead of tearing the circuit down mid-drag.
        EdlSegment updated;
        if (leftEdge)
        {
            var hi = Math.Max(min, segment.SrcEnd - 0.1);
            updated = segment with { SrcStart = Math.Clamp(newSourceTime, Math.Min(min, hi), hi) };
        }
        else
        {
            var lo = segment.SrcStart + 0.1;
            updated = segment with { SrcEnd = Math.Clamp(newSourceTime, lo, Math.Max(lo, max)) };
        }
        Apply(doc => doc with
        {
            Segments = doc.Segments.Select(s => s.Id == id ? updated : s).ToList(),
        });
    }

    /// <summary>Slip: shift the segment's source window, duration unchanged.</summary>
    public void SlipSegment(string id, double sourceDelta)
    {
        if (Doc is null) return;
        var index = IndexOfSegment(id);
        if (index < 0) return;
        var segment = Doc.Segments[index];
        var min = index > 0 ? Doc.Segments[index - 1].SrcEnd : 0;
        var max = index < Doc.Segments.Count - 1
            ? Doc.Segments[index + 1].SrcStart
            : Meta?.SourceDuration ?? segment.SrcEnd;
        var length = segment.SrcEnd - segment.SrcStart;
        var start = Math.Clamp(segment.SrcStart + sourceDelta, min, Math.Max(min, max - length));
        var updated = segment with { SrcStart = start, SrcEnd = start + length };
        Apply(doc => doc with
        {
            Segments = doc.Segments.Select(s => s.Id == id ? updated : s).ToList(),
        });
    }

    public void SetSegmentSpeed(string id, double speed) => Apply(doc => doc with
    {
        Segments = doc.Segments
            .Select(s => s.Id == id ? s with { Speed = Math.Clamp(speed, 0.5, 2.0) } : s)
            .ToList(),
    }, coalesceKey: $"speed:{id}");

    public void AddText(double sourceTime)
    {
        var id = NewId("t");
        var end = Meta?.SourceDuration is > 0 and var duration
            ? Math.Min(sourceTime + 2.5, duration)
            : sourceTime + 2.5;
        if (end <= sourceTime) end = sourceTime + 0.5; // never an inverted window
        Apply(doc => doc with
        {
            Texts = doc.Texts.Append(new EdlText(id, sourceTime, end, "New text", 0.4)).ToList(),
        });
        SelectedId = id;
    }

    public void AddMarker(double sourceTime)
    {
        var id = NewId("m");
        Apply(doc => doc with
        {
            Markers = doc.Markers.Append(new EdlMarker(id, sourceTime,
                $"M{doc.Markers.Count + 1}")).ToList(),
        });
        SelectedId = id;
    }

    public void UpdateCaptionText(string id, string text) => Apply(doc => doc with
    {
        Captions = doc.Captions.Select(c => c.Id == id ? c with { Text = text } : c).ToList(),
    }, coalesceKey: $"caption:{id}");

    public void UpdateTextOverlay(string id, string? text = null, double? y = null) => Apply(doc => doc with
    {
        Texts = doc.Texts.Select(t => t.Id == id
            ? t with { Text = text ?? t.Text, Y = y ?? t.Y }
            : t).ToList(),
    }, coalesceKey: $"text:{id}");

    public void MoveWindow(string id, double sourceDelta)
    {
        if (Doc is null) return;
        if (Doc.Captions.FirstOrDefault(c => c.Id == id) is { } caption)
        {
            var duration = caption.End - caption.Start;
            var start = Math.Max(0, caption.Start + sourceDelta);
            Apply(doc => doc with
            {
                Captions = doc.Captions.Select(c => c.Id == id
                    ? c with { Start = start, End = start + duration }
                    : c).ToList(),
            });
        }
        else if (Doc.Texts.FirstOrDefault(t => t.Id == id) is { } text)
        {
            var duration = text.End - text.Start;
            var start = Math.Max(0, text.Start + sourceDelta);
            Apply(doc => doc with
            {
                Texts = doc.Texts.Select(t => t.Id == id
                    ? t with { Start = start, End = start + duration }
                    : t).ToList(),
            });
        }
    }

    public void SetCaptionStyle(string style) => Apply(doc => doc with { CaptionStyle = style });

    public void SetReframe(string reframe) => Apply(doc => doc with { Reframe = reframe });

    public void SetTransform(double? scale = null, double? posX = null) => Apply(doc => doc with
    {
        Transform = new EdlTransform(
            Math.Clamp(scale ?? doc.Transform.Scale, 1, 3),
            Math.Clamp(posX ?? doc.Transform.PosX, -1, 1)),
    }, coalesceKey: "transform");

    public void SetAudio(double? voice = null, double? music = null) => Apply(doc => doc with
    {
        Audio = new EdlAudio(
            Math.Clamp(voice ?? doc.Audio.Voice, 0, 2),
            Math.Clamp(music ?? doc.Audio.Music, 0, 1)),
    }, coalesceKey: "audio");

    /// <summary>Pull handle material onto the timeline: move the first
    /// segment's in-point earlier into the padded master (clamped at 0).</summary>
    public void ExtendStart(double seconds)
    {
        if (Doc is null || Doc.Segments.Count == 0) return;
        var first = Doc.Segments[0];
        var target = Math.Max(0, first.SrcStart - seconds);
        if (target >= first.SrcStart - 1e-6) return;
        TrimSegment(first.Id, leftEdge: true, target);
        RequestSeek(target + 0.001); // reveal the new opening frame
    }

    public void ExtendEnd(double seconds)
    {
        if (Doc is null || Doc.Segments.Count == 0) return;
        var last = Doc.Segments[^1];
        var max = Meta?.SourceDuration is { } known && known > 0 ? known : last.SrcEnd;
        var target = Math.Min(max, last.SrcEnd + seconds);
        if (target <= last.SrcEnd + 1e-6) return;
        TrimSegment(last.Id, leftEdge: false, target);
        RequestSeek(Math.Max(0, target - 1.0)); // land just before the new tail
    }

    /// <summary>Mark in/out: trim the segment under the playhead to start or end
    /// at the playhead.</summary>
    public void MarkIn() => TrimAtPlayhead(leftEdge: true);
    public void MarkOut() => TrimAtPlayhead(leftEdge: false);

    private void TrimAtPlayhead(bool leftEdge)
    {
        if (Doc is null) return;
        var segment = Doc.Segments.FirstOrDefault(s =>
            Playhead >= s.SrcStart - 1e-6 && Playhead <= s.SrcEnd + 1e-6);
        if (segment is null) return;
        TrimSegment(segment.Id, leftEdge, Playhead);
    }

    // ---- persistence ------------------------------------------------------ //

    private void MarkDirty()
    {
        SaveState = "Unsaved";
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => _ = FlushSaveAsync(), null,
            TimeSpan.FromSeconds(1.5), Timeout.InfiniteTimeSpan);
    }

    public async Task FlushSaveAsync()
    {
        if (Doc is null || Meta is null || SaveState is "Saved" or "Saving…") return;
        var token = _loadToken;
        var clipId = Meta.ClipId;
        var doc = Doc;
        SaveState = "Saving…";
        notify();
        try
        {
            var response = await api.SaveEditorDocAsync(ProjectIdOrThrow(), clipId, doc);
            // Another clip may have loaded while the save was in flight — its
            // state is not ours to touch (the save itself still landed).
            if (_loadToken != token || Meta is null) return;
            Meta = response with { Doc = Meta.Doc };
            SaveState = "Saved";
        }
        catch (ApiException exception)
        {
            if (_loadToken != token) return;
            SaveState = "Save failed";
            Error = exception.UserMessage;
        }
        catch (Exception)
        {
            // Any escape here would wedge SaveState at "Saving…" forever.
            if (_loadToken != token) return;
            SaveState = "Save failed";
            Error = "Couldn't save the edit — it will retry on the next change.";
        }
        finally
        {
            notify();
        }
    }

    private Guid _projectId;
    public void Bind(Guid projectId) => _projectId = projectId;
    private Guid ProjectIdOrThrow() => _projectId != Guid.Empty
        ? _projectId
        : throw new InvalidOperationException("editor session has no project");

    // ---- export ------------------------------------------------------------ //

    public async Task ExportAsync()
    {
        if (Doc is null || Meta is null || ExportBusy) return;
        var token = _loadToken;
        var clipId = Meta.ClipId;
        ExportBusy = true;
        ExportError = null;
        ExportDone = false;
        ExportDoneUrl = null;
        notify();
        try
        {
            var job = await api.ExportEditAsync(_projectId, clipId, Doc);
            ExportJobId = job.Id;
            SaveState = "Saved"; // export persists the doc server-side
            notify();
            for (var i = 0; i < 360; i++) // ≤ 30 min
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (_loadToken != token) return; // a different cut is open now
                var state = await api.GetJobAsync(job.Id);
                if (state.State is "starting" or "running") continue;
                if (state.State == "succeeded")
                {
                    var refreshed = await api.GetEditorDocAsync(_projectId, clipId);
                    if (_loadToken != token) return;
                    Meta = refreshed with { Doc = Doc };
                    ExportDone = true;
                    ExportDoneUrl = clipId is null ? null : refreshed.Export?.Url;
                }
                else if (_loadToken == token)
                {
                    ExportError = "The export didn't finish — try again.";
                }
                return;
            }
            if (_loadToken == token) ExportError = "The export timed out — try again.";
        }
        catch (ApiException exception)
        {
            if (_loadToken == token) ExportError = exception.UserMessage;
        }
        catch (Exception)
        {
            if (_loadToken == token) ExportError = "The export failed — try again.";
        }
        finally
        {
            if (_loadToken == token) ExportBusy = false;
            notify();
        }
    }

    // ---- helpers ------------------------------------------------------------ //

    private int IndexOfSegment(string id)
    {
        if (Doc is null) return -1;
        for (var i = 0; i < Doc.Segments.Count; i++)
            if (Doc.Segments[i].Id == id) return i;
        return -1;
    }

    private static string NewId(string prefix) =>
        $"{prefix}_{Guid.NewGuid().ToString("N")[..6]}";
}
