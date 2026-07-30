namespace Highlighter.Pipeline;

/// <summary>Supabase storage uploads with the project's object-size cap in
/// mind. The cap was measured empirically (50 MiB uploads succeed, 51 MiB
/// returns 413 EntityTooLarge — the Supabase default global limit), and the
/// guard sits 1 MiB under it. Short-form deliverables are re-encoded to fit
/// so they always stream from Supabase; long-form finals instead fall back to
/// the API's local /media mirror, where full quality matters more than CDN
/// hosting.</summary>
public static class Uploads
{
    // Measured cap: 52,428,800 bytes (50 MiB). Guard = cap − 1 MiB.
    private const long DEFAULT_MAX_STORAGE_UPLOAD_BYTES = 49L * 1024 * 1024;

    public static long MaxStorageUploadBytes()
    {
        var raw = Config.EnvOrNull("HIGHLIGHTER_MAX_UPLOAD_BYTES");
        return raw is not null && long.TryParse(raw, out var value) && value > 0
            ? value
            : DEFAULT_MAX_STORAGE_UPLOAD_BYTES;
    }

    /// <summary>URL for a render served from the API's /media mirror of
    /// outputs/ (HIGHLIGHTER_MEDIA_BASE overrides the local default).</summary>
    public static string LocalMediaUrl(string path)
    {
        var relative = Path
            .GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(path))
            .Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("outputs/")) relative = relative["outputs/".Length..];
        var mediaBase = Environment.GetEnvironmentVariable("HIGHLIGHTER_MEDIA_BASE");
        if (string.IsNullOrWhiteSpace(mediaBase)) mediaBase = "http://localhost:5199/media";
        return $"{mediaBase.TrimEnd('/')}/{relative}";
    }

    /// <summary>Upload a rendered video, falling back to its local /media URL
    /// when it exceeds the storage size cap or the upload fails. Never throws —
    /// a failed upload must not cost the row that references the render.</summary>
    public static string UploadVideoOrLocalUrl(
        SupabaseClient db, string bucket, string key, string path, string label)
    {
        if (new FileInfo(path).Length > MaxStorageUploadBytes())
        {
            Console.WriteLine($"{label} exceeds the storage size cap; serving via local /media.");
            return LocalMediaUrl(path);
        }
        try
        {
            return db.UploadStorageObject(bucket: bucket, key: key, path: path);
        }
        catch (Exception exc)
        {
            Console.WriteLine($"{label} upload failed; serving via local /media: {exc.Message}");
            return LocalMediaUrl(path);
        }
    }

    /// <summary>A path guaranteed to fit under the storage cap: the original
    /// when it already fits, else a one-shot bitrate-targeted re-encode sized
    /// to the clip's duration (audio copied). Falls back to the original on
    /// any ffmpeg failure.</summary>
    public static string FitForUpload(string path, double durationSeconds)
    {
        var cap = MaxStorageUploadBytes();
        if (new FileInfo(path).Length <= cap) return path;
        if (durationSeconds <= 0) return path;

        // 92% of the cap's bit budget minus the audio track's share.
        var videoBitrate = Math.Max(
            300_000L, (long)(cap * 8 / durationSeconds * 0.92) - 128_000L);
        var fittedPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path))!,
            $"{Path.GetFileNameWithoutExtension(path)}_fit.mp4");
        var (code, stdout, stderr) = Proc.Run(new[]
        {
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-i", path,
            "-c:v", "libx264", "-preset", "veryfast",
            "-b:v", videoBitrate.ToString(),
            "-maxrate", videoBitrate.ToString(),
            "-bufsize", (videoBitrate * 2).ToString(),
            "-c:a", "copy",
            "-movflags", "+faststart",
            fittedPath,
        });
        if (code != 0 || !File.Exists(fittedPath))
        {
            var details = (stderr.Length > 0 ? stderr : stdout).Trim();
            Console.WriteLine($"Fit re-encode failed for {Path.GetFileName(path)}: {details}");
            return path;
        }
        Console.WriteLine(
            $"Re-encoded {Path.GetFileName(path)} to fit the storage cap "
            + $"({videoBitrate / 1000} kbps video)");
        return fittedPath;
    }

    /// <summary>Upload a short-form deliverable under its original storage key,
    /// re-encoding first when it would blow the cap. Never throws.</summary>
    public static string UploadFittedOrLocalUrl(
        SupabaseClient db, string bucket, string key, string path, string label,
        double durationSeconds)
    {
        var uploadPath = FitForUpload(path, durationSeconds);
        if (new FileInfo(uploadPath).Length > MaxStorageUploadBytes())
        {
            Console.WriteLine($"{label} exceeds the storage size cap; serving via local /media.");
            return LocalMediaUrl(path);
        }
        try
        {
            return db.UploadStorageObject(bucket: bucket, key: key, path: uploadPath);
        }
        catch (Exception exc)
        {
            Console.WriteLine($"{label} upload failed; serving via local /media: {exc.Message}");
            return LocalMediaUrl(path);
        }
    }
}
