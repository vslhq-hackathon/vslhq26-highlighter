using System.Globalization;

namespace Highlighter.Web.Models;

/// <summary>API values → the display strings the studio UI was designed around.</summary>
public static class Fmt
{
    /// <summary>"2:01:44", "24:36", "0:42" — or "—" until the source is probed.
    /// TimeSpan.FromSeconds throws on NaN/Infinity, so guard first.</summary>
    public static string Duration(double? seconds)
    {
        if (seconds is not > 0 || !double.IsFinite(seconds.Value)) return "—";
        var t = TimeSpan.FromSeconds(seconds.Value);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Zero-padded position stamp for lists: "05:38", "1:02:11".</summary>
    public static string Stamp(double seconds)
    {
        if (!double.IsFinite(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>Editor timecode "00:00:12:04" — a frame counter at the media's
    /// real rate (non-drop-frame, so 29.97 counts 0..29 like an NDF timecode).</summary>
    public static string Timecode(double seconds, double fps = 30)
    {
        if (!double.IsFinite(seconds)) seconds = 0;
        var clamped = Math.Max(0, seconds);
        var t = TimeSpan.FromSeconds(clamped);
        var frameRate = double.IsFinite(fps) && fps >= 1 ? (int)Math.Round(fps) : 30;
        var frames = (int)Math.Round(t.Milliseconds / 1000.0 * frameRate) % frameRate;
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}:{frames:00}";
    }

    /// <summary>Pipeline clip scores are 0..1 doubles; the UI shows 0–100.</summary>
    public static int Score100(double? score) => (int)Math.Round((score ?? 0) * 100);

    /// <summary>SampleData's exact opacity ramp, kept for visual continuity.</summary>
    public static string ScoreOpacity(double? score) =>
        (0.35 + 0.65 * Math.Max(0, (Score100(score) - 50) / 50.0))
            .ToString("0.00", CultureInfo.InvariantCulture);

    public static string Kind(string sourceType) => sourceType.ToUpperInvariant();

    public static string Outputs(string pipeline) => pipeline switch
    {
        "both" => "Highlights + Long-form",
        "long" => "Long-form",
        _ => "Highlights",
    };

    public static string StatusLine(ProjectSummaryDto p) => p.Status switch
    {
        "created" => "Queued",
        "ingesting" => p.Progress.Percent is { } pct ? $"Editing · {pct * 100:0}%" : "Editing",
        "stopping" => "Stopping",
        "ready" => "Ready",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        var s when s.Length > 0 => char.ToUpperInvariant(s[0]) + s[1..],
        _ => "",
    };

    public static string Added(DateTimeOffset createdAt) =>
        "added " + createdAt.LocalDateTime.ToString("MMM d", CultureInfo.InvariantCulture);

    public static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>The pipeline's error column is raw tool output (ffmpeg/yt-dlp
    /// stderr, absolute paths). Map it to a plain one-liner for the UI; the full
    /// text stays in the worker logs.</summary>
    public static string FriendlyPipelineError(string raw)
    {
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("sign in to confirm") || lower.Contains("bot"))
            return "The source platform blocked the download — try again, or use another video.";
        if (lower.Contains("yt-dlp") || lower.Contains("streamlink")
            || lower.Contains("unable to download") || lower.Contains("403"))
            return "The source video couldn't be downloaded.";
        if (lower.Contains("worker exited") || lower.Contains("worker terminated")
            || lower.Contains("spawn failed"))
            return "The processing run stopped unexpectedly.";
        if (lower.Contains("transcri") || lower.Contains("speech"))
            return "Transcription failed while processing the audio.";
        if (lower.Contains("ffmpeg")) return "Video processing failed.";
        if (lower.Contains("supabase") || lower.Contains("postgrest") || lower.Contains("storage"))
            return "Saving results to the database failed.";
        // Unknown cause: show a trimmed first line, never a wall of tool output.
        var firstLine = raw.Split('\n')[0].Trim();
        return firstLine.Length is > 0 and <= 120
            ? firstLine
            : "Processing failed — the run's logs have the details.";
    }
}
