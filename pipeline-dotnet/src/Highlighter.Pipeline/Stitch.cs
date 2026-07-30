namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/stitch.py.
///
/// Stitch rendered clip MP4s into one long-form video.
///
/// Clips come from Render with identical encode settings (h264/aac, same scale
/// filter), so a codec-copy concat normally works; when it doesn't (e.g. the
/// source changed resolution mid-VOD), fall back to a re-encode.</summary>
public static class Stitch
{
    public static void StitchClips(IReadOnlyList<string> clipPaths, string outputPath)
    {
        if (clipPaths.Count == 0)
            throw new PipelineError("No clips to stitch");
        var missing = clipPaths.Where(path => !File.Exists(path)).ToList();
        if (missing.Count > 0)
            throw new PipelineError($"Missing clips for stitching: {string.Join(", ", missing)}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var tmpdir = new TempDir(parent: Path.GetDirectoryName(Path.GetFullPath(outputPath)));
        var concatList = Path.Combine(tmpdir.Path, "clips.txt");
        File.WriteAllText(concatList, string.Concat(
            clipPaths.Select(path => $"file '{ConcatPath(path)}'\n")));

        var copyResult = RunFfmpeg(concatList, outputPath, reencode: false);
        if (copyResult.Code != 0)
        {
            Console.WriteLine("Codec-copy stitch failed; re-encoding the long-form video");
            var reencodeResult = RunFfmpeg(concatList, outputPath, reencode: true);
            if (reencodeResult.Code != 0)
            {
                var details = reencodeResult.Stderr.Trim();
                throw new PipelineError(
                    details.Length > 0 ? details : "ffmpeg failed while stitching clips");
            }
        }
    }

    private static (int Code, string Stdout, string Stderr) RunFfmpeg(
        string concatList, string outputPath, bool reencode)
    {
        // The re-encode fallback fires exactly when clip resolutions differ
        // mid-VOD, so it also normalizes everything to the 720p working cap.
        var codecArgs = reencode
            ? new[]
            {
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "30",
                "-vf", "scale='min(720,iw)':-2", "-c:a", "aac", "-b:a", "128k",
            }
            : new[] { "-c", "copy" };
        var command = new List<string>
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
        };
        command.AddRange(codecArgs);
        command.AddRange(new[] { "-movflags", "+faststart", outputPath });
        return Proc.Run(command);
    }

    private static string ConcatPath(string path) =>
        Path.GetFullPath(path).Replace("'", "'\\''");
}
