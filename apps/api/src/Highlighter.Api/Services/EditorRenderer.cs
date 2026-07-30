using System.Diagnostics;
using System.Globalization;
using System.Text;
using Highlighter.Api.Contracts;

namespace Highlighter.Api.Services;

/// <summary>Renders an EditorDoc against its source media with ffmpeg: trims and
/// concatenates segments (with per-segment speed), applies the manual-reframe
/// zoom/pan, burns captions (generated .ass — boxed/plain/karaoke) and text
/// overlays, scales voice volume and mixes an ambient music bed, then grabs a
/// thumbnail. Argument/filter builders are pure and unit-tested; only Run
/// touches the system.</summary>
public sealed class EditorRenderer(ILogger<EditorRenderer> log)
{
    public sealed record SourceInfo(
        double Duration, int Width, int Height, bool HasAudio, double? Fps = null);

    /// <summary>ffprobe reports frame rate as a fraction ("30000/1001", "25/1");
    /// null on "0/0" or anything unparsable.</summary>
    public static double? ParseFrameRate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator))
            return denominator > 0 && numerator > 0 ? numerator / denominator : null;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value > 0 ? value : null;
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- probing --------------------------------------------------------- //

    public async Task<SourceInfo> ProbeAsync(string path, Action<string> logLine, CancellationToken ct)
    {
        var output = await RunAsync("ffprobe",
        [
            "-v", "error",
            "-show_entries", "stream=codec_type,width,height,avg_frame_rate:format=duration",
            "-of", "json", path,
        ], logLine, ct);
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        double duration = 0;
        if (doc.RootElement.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        int width = 0, height = 0;
        double? fps = null;
        var hasAudio = false;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.GetProperty("codec_type").GetString();
                if (type == "video" && width == 0)
                {
                    width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    if (stream.TryGetProperty("avg_frame_rate", out var rate))
                        fps = ParseFrameRate(rate.GetString());
                }
                if (type == "audio") hasAudio = true;
            }
        }
        if (width == 0 || height == 0 || duration <= 0)
            throw new InvalidOperationException($"could not probe {path}");
        return new SourceInfo(duration, width, height, hasAudio, fps);
    }

    // ---- pure builders --------------------------------------------------- //

    /// <summary>A rasterized overlay to composite: FileName is relative to the
    /// work dir (an extra ffmpeg input), visible over [Start, End) output
    /// seconds, at YExpr (an ffmpeg expression over main_h/overlay_h).</summary>
    public sealed record OverlaySpec(string FileName, double Start, double End, string YExpr);

    /// <summary>The full -filter_complex expression. Inputs: 0 = source,
    /// 1 = music pad (only when doc.Audio.Music > 0), then one input per
    /// overlay PNG starting at overlayInputBase.</summary>
    public static string BuildFilterGraph(EditorDoc doc, SourceInfo source,
        IReadOnlyList<OverlaySpec> overlays, int overlayInputBase)
    {
        var chains = new List<string>();
        var concatInputs = new StringBuilder();
        for (var i = 0; i < doc.Segments.Count; i++)
        {
            var segment = doc.Segments[i];
            var video = $"[0:v]trim=start={F(segment.SrcStart)}:end={F(segment.SrcEnd)},"
                + $"setpts=(PTS-STARTPTS)/{F(segment.Speed)}[v{i}]";
            chains.Add(video);
            concatInputs.Append($"[v{i}]");
            if (source.HasAudio)
            {
                var audio = $"[0:a]atrim=start={F(segment.SrcStart)}:end={F(segment.SrcEnd)},"
                    + $"asetpts=PTS-STARTPTS,atempo={F(Math.Clamp(segment.Speed, 0.5, 2.0))}[a{i}]";
                chains.Add(audio);
                concatInputs.Append($"[a{i}]");
            }
        }
        chains.Add($"{concatInputs}concat=n={doc.Segments.Count}:v=1:a={(source.HasAudio ? 1 : 0)}"
            + $"[vcat]{(source.HasAudio ? "[acat]" : "")}");

        // Video finishing chain: reframe zoom/pan, then overlay compositing.
        var videoOps = new List<string>();
        if (doc.Reframe == "manual" && doc.Transform.Scale > 1.001)
        {
            var scale = doc.Transform.Scale;
            var xFraction = Math.Clamp(0.5 + doc.Transform.PosX / 2.0, 0, 1);
            videoOps.Add($"crop=w=iw/{F(scale)}:h=ih/{F(scale)}"
                + $":x=(iw-iw/{F(scale)})*{F(xFraction)}:y=(ih-ih/{F(scale)})/2");
            videoOps.Add($"scale={source.Width}:{source.Height}");
        }
        var current = "[vbase]";
        chains.Add($"[vcat]{string.Join(",", videoOps.Count > 0 ? videoOps : ["null"])}{current}");
        for (var i = 0; i < overlays.Count; i++)
        {
            var spec = overlays[i];
            var next = i == overlays.Count - 1 ? "[vover]" : $"[vo{i}]";
            chains.Add($"{current}[{overlayInputBase + i}:v]overlay=x=(main_w-overlay_w)/2"
                + $":y={spec.YExpr}:enable='between(t,{F(spec.Start)},{F(spec.End)})'{next}");
            current = next;
        }
        chains.Add($"{current}format=yuv420p[vout]");

        // Audio finishing chain — whatever the shape, the mapped label is [aout].
        var withMusic = doc.Audio.Music > 0.001;
        if (source.HasAudio && withMusic)
        {
            chains.Add($"[acat]volume={F(doc.Audio.Voice)}[amain]");
            chains.Add($"[1:a]volume={F(doc.Audio.Music * 0.5)}[amusic]");
            chains.Add("[amain][amusic]amix=inputs=2:duration=first:dropout_transition=0"
                + ",alimiter=limit=0.95[aout]");
        }
        else if (source.HasAudio)
        {
            chains.Add($"[acat]volume={F(doc.Audio.Voice)}[aout]");
        }
        else if (withMusic)
        {
            chains.Add($"[1:a]volume={F(doc.Audio.Music * 0.5)}[aout]");
        }
        return string.Join(";", chains);
    }

    public static List<string> BuildRenderArgs(
        EditorDoc doc, SourceInfo source, string sourcePath, string? padPath,
        IReadOnlyList<OverlaySpec> overlays, string outputPath)
    {
        var withMusic = doc.Audio.Music > 0.001 && padPath is not null;
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-i", sourcePath };
        if (withMusic) args.AddRange(["-stream_loop", "-1", "-i", padPath!]);
        var overlayInputBase = withMusic ? 2 : 1;
        foreach (var spec in overlays) args.AddRange(["-i", spec.FileName]);
        args.AddRange(["-filter_complex",
            BuildFilterGraph(doc, source, overlays, overlayInputBase)]);
        args.AddRange(["-map", "[vout]"]);
        if (source.HasAudio || withMusic) args.AddRange(["-map", "[aout]"]);
        if (withMusic && !source.HasAudio)
            args.AddRange(["-t", F(EditorDocs.OutputDuration(doc))]);
        args.AddRange(
        [
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-c:a", "aac", "-b:a", "160k",
            "-movflags", "+faststart",
            outputPath,
        ]);
        return args;
    }

    /// <summary>What to rasterize and when to show it. Caption lines become one
    /// overlay each; karaoke becomes one overlay per word-state (the sweep),
    /// degrading to line-level when the plan would exceed maxInputs (ffmpeg
    /// input/filter-graph sanity for long documents).</summary>
    public sealed record OverlayPlanItem(
        string FileName, double Start, double End, string YExpr,
        string Kind, string Text, IReadOnlyList<string>? Words, int HighlightWords);

    public const string CaptionYExpr = "main_h*0.86-overlay_h";

    public static List<OverlayPlanItem> PlanOverlays(EditorDoc doc, int maxInputs = 240)
    {
        var items = new List<OverlayPlanItem>();
        var karaoke = doc.CaptionStyle == "karaoke";
        if (karaoke)
        {
            var stateCount = doc.Captions.Sum(caption => Math.Max(1, caption.Words?.Count ?? 1))
                + doc.Texts.Count;
            if (stateCount > maxInputs) karaoke = false; // degrade: line-level accent
        }

        var index = 0;
        foreach (var caption in doc.Captions)
        {
            if (string.IsNullOrWhiteSpace(caption.Text)) continue;
            var line = EditorDocs.MapWindow(doc, caption.Start, caption.End);
            if (line is not { } visible) continue;
            var words = caption.Words?.Select(word => word.T).ToList();

            if (karaoke && caption.Words is { Count: > 1 })
            {
                for (var state = 0; state < caption.Words.Count; state++)
                {
                    var stateStart = EditorDocs.SourceToOutput(doc, caption.Words[state].S);
                    if (stateStart is null) continue; // word sits in a cut
                    var stateEnd = state + 1 < caption.Words.Count
                        ? EditorDocs.SourceToOutput(doc, caption.Words[state + 1].S) ?? visible.End
                        : visible.End;
                    if (stateEnd <= stateStart) continue;
                    items.Add(new OverlayPlanItem($"ov_{index++:000}.png",
                        Math.Max(visible.Start, stateStart.Value), Math.Min(visible.End, stateEnd),
                        CaptionYExpr, "caption", caption.Text, words, state + 1));
                }
            }
            else
            {
                items.Add(new OverlayPlanItem($"ov_{index++:000}.png",
                    visible.Start, visible.End, CaptionYExpr, "caption", caption.Text,
                    words, doc.CaptionStyle == "karaoke" ? words?.Count ?? 0 : 0));
            }
        }

        foreach (var text in doc.Texts)
        {
            if (string.IsNullOrWhiteSpace(text.Text)) continue;
            var window = EditorDocs.MapWindow(doc, text.Start, text.End);
            if (window is not { } visible) continue;
            items.Add(new OverlayPlanItem($"ov_{index++:000}.png",
                visible.Start, visible.End, $"main_h*{F(text.Y)}", "text", text.Text, null, 0));
        }
        return items;
    }

    /// <summary>One-shot bitrate-targeted re-encode used when an export exceeds
    /// the Supabase object-size cap (audio copied untouched).</summary>
    public static List<string> BuildFitArgs(string inputPath, string outputPath, long videoBitrate) =>
    [
        "-hide_banner", "-loglevel", "warning", "-y",
        "-i", inputPath,
        "-c:v", "libx264", "-preset", "veryfast",
        "-b:v", videoBitrate.ToString(CultureInfo.InvariantCulture),
        "-maxrate", videoBitrate.ToString(CultureInfo.InvariantCulture),
        "-bufsize", (videoBitrate * 2).ToString(CultureInfo.InvariantCulture),
        "-c:a", "copy",
        "-movflags", "+faststart",
        outputPath,
    ];

    public static List<string> BuildThumbnailArgs(string videoPath, double atSeconds, string outputPath) =>
    [
        "-hide_banner", "-loglevel", "warning", "-y",
        "-ss", F(Math.Max(0, atSeconds)), "-i", videoPath,
        "-frames:v", "1", "-update", "1", "-q:v", "4", outputPath,
    ];

    /// <summary>The synthesized ambient pad (once per scratch dir): three soft
    /// sine layers with slow tremolo — an honest, license-free "music bed".</summary>
    public static List<string> BuildPadArgs(string outputPath) =>
    [
        "-hide_banner", "-loglevel", "warning", "-y",
        "-f", "lavfi", "-i", "sine=frequency=196:duration=8",
        "-f", "lavfi", "-i", "sine=frequency=294:duration=8",
        "-f", "lavfi", "-i", "sine=frequency=392:duration=8",
        "-filter_complex",
        "[0:a][1:a][2:a]amix=inputs=3:duration=first,tremolo=f=0.15:d=0.5,"
        + "lowpass=f=1200,volume=0.5,afade=t=in:d=1.2,afade=t=out:st=6.5:d=1.5[a]",
        "-map", "[a]", outputPath,
    ];

    // ---- execution ------------------------------------------------------- //

    public async Task<string> RunAsync(
        string fileName, IReadOnlyList<string> args, Action<string> logLine, CancellationToken ct,
        string? workingDirectory = null)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? "",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in args) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) logLine(e.Data!);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation only stops the wait — the child must die with it.
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        if (process.ExitCode != 0)
        {
            log.LogError("{Tool} exited {Code}", fileName, process.ExitCode);
            throw new InvalidOperationException($"{fileName} exited with {process.ExitCode}");
        }
        return stdout.ToString();
    }
}
