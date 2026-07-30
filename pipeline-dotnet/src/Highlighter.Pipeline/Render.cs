namespace Highlighter.Pipeline;

/// <summary>Encode quality tiers. Working is the pre-bump pipeline quality —
/// intermediates and the long-form assembly path, where the codec-copy stitch
/// makes the trim/render settings the final settings. Delivery is for
/// short-form deliverables users stream from Supabase.</summary>
public enum EncodeProfile
{
    Working,
    Delivery,
}

/// <summary>Port of highlighter_pipeline/render.py.</summary>
public static class Render
{
    // Both profiles cap at 720p: capture uses yt-dlp's pre-merged best format,
    // which never exceeds 720 wide, so a bigger cap only upscales.
    private const string SCALE_720 = "scale='min(720,iw)':-2";

    private static string ProfileCrf(EncodeProfile profile) =>
        profile == EncodeProfile.Delivery ? "23" : "30";

    /// <summary>A combined run passes suffix ("_short"/"_long") so the two
    /// forks never collide on a filename when they pick the same window.</summary>
    public static string ClipFilename(
        int chunkIndex, double startSeconds, double endSeconds, string suffix = "")
    {
        var startMs = (int)Math.Round(startSeconds * 1000, MidpointRounding.ToEven);
        var endMs = (int)Math.Round(endSeconds * 1000, MidpointRounding.ToEven);
        return $"clip_{chunkIndex:00000}_{startMs}_{endMs}{suffix}.mp4";
    }

    public static void RenderClipFromVideoUrl(
        string sourceUrl, string outputPath, double startSeconds, double endSeconds)
    {
        ValidateWindow(startSeconds: startSeconds, endSeconds: endSeconds);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        using var tmpdir = new TempDir(parent: Path.GetDirectoryName(Path.GetFullPath(outputPath)));
        var template = Path.Combine(tmpdir.Path, "clip.%(ext)s");
        var command = new List<string>
        {
            "yt-dlp",
            "--quiet",
            "--no-warnings",
            "--no-playlist",
        };
        command.AddRange(Cookies.YtdlpCookieArgs());
        command.AddRange(new[]
        {
            "-f",
            "bv*+ba/b",
            "--download-sections",
            $"*{FormatTimestamp(startSeconds)}-{FormatTimestamp(endSeconds)}",
            "--force-keyframes-at-cuts",
            "--merge-output-format",
            "mp4",
            "-o",
            template,
            sourceUrl,
        });
        RunChecked(command);

        var candidates = Directory.GetFiles(tmpdir.Path, "clip.*")
            .OrderBy(path => File.GetLastWriteTimeUtc(path))
            .ToList();
        if (candidates.Count == 0)
            throw new PipelineError("yt-dlp did not produce a rendered clip");

        var rendered = candidates[^1];
        TranscodeToMp4(sourcePath: rendered, outputPath: outputPath);
    }

    /// <summary>Render a clip window that may span several consecutive archive segments.
    ///
    /// Segments were cut on keyframes, so nominal offsets can drift by up to a
    /// keyframe interval (~2s) from true stream time.</summary>
    public static void RenderClipFromSegments(
        IReadOnlyList<string> segmentPaths,
        string outputPath,
        double firstSegmentStartSeconds,
        double startSeconds,
        double endSeconds,
        EncodeProfile profile = EncodeProfile.Working)
    {
        ValidateWindow(startSeconds: startSeconds, endSeconds: endSeconds);
        var missing = segmentPaths.Where(path => !File.Exists(path)).ToList();
        if (missing.Count > 0)
            throw new PipelineError(
                $"Missing source segments for clip render: {string.Join(", ", missing)}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var relativeStart = Math.Max(0, startSeconds - firstSegmentStartSeconds);
        var duration = endSeconds - startSeconds;

        using var tmpdir = new TempDir(parent: Path.GetDirectoryName(Path.GetFullPath(outputPath)));
        var concatList = Path.Combine(tmpdir.Path, "segments.txt");
        File.WriteAllText(concatList, string.Concat(
            segmentPaths.Select(path => $"file '{Path.GetFullPath(path)}'\n")));
        RunChecked(new List<string>
        {
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-f",
            "concat",
            "-safe",
            "0",
            "-i",
            concatList,
            "-ss",
            Py.F(relativeStart, 3),
            "-t",
            Py.F(duration, 3),
            "-map",
            "0:v:0",
            "-map",
            "0:a:0?",
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-crf",
            ProfileCrf(profile),
            "-vf",
            SCALE_720,
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            "-movflags",
            "+faststart",
            outputPath,
        });
    }

    /// <summary>Re-encode a window of an already-rendered clip (same encode settings as
    /// Working-profile renders, so trimmed segments still codec-copy concat with
    /// untrimmed long-fork ones).</summary>
    public static void TrimClip(
        string sourcePath, string outputPath, double startOffsetSeconds, double durationSeconds)
    {
        if (durationSeconds <= 0)
            throw new PipelineError("Trim duration must be greater than 0");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        RunChecked(new List<string>
        {
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            sourcePath,
            "-ss",
            Py.F(Math.Max(0.0, startOffsetSeconds), 3),
            "-t",
            Py.F(durationSeconds, 3),
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-crf",
            "30",
            "-vf",
            SCALE_720,
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            "-movflags",
            "+faststart",
            outputPath,
        });
    }

    /// <summary>Extract a single JPEG frame (maxWidth wide at most) from a rendered clip.</summary>
    public static void ExtractThumbnail(string clipPath, string outputPath, double atSeconds, int maxWidth = 480)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        RunChecked(new List<string>
        {
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-ss",
            Py.F(Math.Max(0.0, atSeconds), 3),
            "-i",
            clipPath,
            "-frames:v",
            "1",
            "-vf",
            $"scale='min({maxWidth},iw)':-2",
            "-q:v",
            "4",
            outputPath,
        });
        if (!File.Exists(outputPath))
            throw new PipelineError("ffmpeg produced no thumbnail frame");
    }

    private static void TranscodeToMp4(string sourcePath, string outputPath)
    {
        RunChecked(new List<string>
        {
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            sourcePath,
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-crf",
            "30",
            "-vf",
            SCALE_720,
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            "-movflags",
            "+faststart",
            outputPath,
        });
    }

    /// <summary>The container's first timestamp in seconds, or null when
    /// unreadable. Archive segments keep the capture run's continuous
    /// timeline, so this is a segment's measured position — the difference
    /// from the first segment's value places it on the same clock the
    /// transcript chunks use.</summary>
    public static double? ProbeStartTime(string path)
    {
        var (code, stdout, _) = Proc.Run(new[]
        {
            "ffprobe",
            "-v",
            "error",
            "-show_entries",
            "format=start_time",
            "-of",
            "csv=p=0",
            path,
        });
        if (code != 0)
            return null;
        return double.TryParse(
            stdout.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    /// <summary>The container's duration in seconds, or null when unreadable.</summary>
    public static double? ProbeDuration(string path)
    {
        var (code, stdout, _) = Proc.Run(new[]
        {
            "ffprobe",
            "-v",
            "error",
            "-show_entries",
            "format=duration",
            "-of",
            "csv=p=0",
            path,
        });
        if (code != 0) return null;
        return double.TryParse(
            stdout.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value) && value > 0
            ? value
            : null;
    }

    /// <summary>The video stream's average frame rate ("30000/1001" → 29.97),
    /// or null when unreadable — the editor uses it for real frame stepping.</summary>
    public static double? ProbeFps(string path)
    {
        var (code, stdout, _) = Proc.Run(new[]
        {
            "ffprobe",
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=avg_frame_rate",
            "-of",
            "csv=p=0",
            path,
        });
        if (code != 0) return null;
        var raw = stdout.Trim();
        var parts = raw.Split('/');
        var style = System.Globalization.NumberStyles.Float;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length == 2
            && double.TryParse(parts[0], style, culture, out var numerator)
            && double.TryParse(parts[1], style, culture, out var denominator))
            return numerator > 0 && denominator > 0 ? numerator / denominator : null;
        return double.TryParse(raw, style, culture, out var value) && value > 0 ? value : null;
    }

    private static void ValidateWindow(double startSeconds, double endSeconds)
    {
        if (endSeconds <= startSeconds)
            throw new PipelineError("Clip end_seconds must be greater than start_seconds");
    }

    public static string FormatTimestamp(double seconds)
    {
        var wholeSeconds = (int)seconds;
        var milliseconds = (int)Math.Round((seconds - wholeSeconds) * 1000, MidpointRounding.ToEven);
        if (milliseconds == 1000)
        {
            wholeSeconds += 1;
            milliseconds = 0;
        }

        var hours = wholeSeconds / 3600;
        var remainder = wholeSeconds % 3600;
        var minutes = remainder / 60;
        var secs = remainder % 60;
        return $"{hours:00}:{minutes:00}:{secs:00}.{milliseconds:000}";
    }

    private static void RunChecked(IReadOnlyList<string> command)
    {
        var (code, stdout, stderr) = Proc.Run(command);
        if (code != 0)
        {
            var details = (stderr.Length > 0 ? stderr : stdout).Trim();
            throw new PipelineError(
                details.Length > 0 ? details : $"Command failed with exit code {code}");
        }
    }
}
