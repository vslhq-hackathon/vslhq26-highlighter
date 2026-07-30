using System.Globalization;
using Highlighter.Api.Contracts;

namespace Highlighter.Api.Services;

/// <summary>Pure argv builders for each worker verb. Values go through
/// ProcessStartInfo.ArgumentList — no shell, no quoting. Ingest never passes the
/// source URL or --min-clip-score: in attach mode the project row wins.</summary>
public static class WorkerArgs
{
    public static List<string> Ingest(Guid projectId, CreateProjectRequest request,
        int? llmConcurrency = null, int? transcribeConcurrency = null)
    {
        var argv = new List<string>
        {
            "ingest",
            "--project-id", projectId.ToString(),
            "--pipeline", request.Pipeline!,
            // Always explicit: the worker's dev default (1) or a stray MAX_CHUNKS
            // in .env would otherwise silently truncate an API-triggered run.
            "--max-chunks", Invariant(request.MaxChunks),
            "--chunk-seconds", Invariant(request.ChunkSeconds),
        };
        if (!string.IsNullOrWhiteSpace(request.TargetMinutes))
            argv.AddRange(["--target-minutes", request.TargetMinutes]);
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            argv.AddRange(["--instructions", request.Instructions]);
        if (request.NoResearch) argv.Add("--no-research");
        if (request.NoShots) argv.Add("--no-shots");
        if (request.NoReframe) argv.Add("--no-reframe");
        if (request.NoCaptions) argv.Add("--no-captions");
        if (request.NoThumbnails) argv.Add("--no-thumbnails");
        if (request.NoArchive) argv.Add("--no-archive");
        // API-call parallelism (measured against the Azure deployments' rate
        // limits, configured via .env) — explicit for the same reason as above.
        if (llmConcurrency is { } llm and > 0)
            argv.AddRange(["--llm-concurrency", Invariant(llm)]);
        if (transcribeConcurrency is { } stt and > 0)
            argv.AddRange(["--transcribe-concurrency", Invariant(stt)]);
        return argv;
    }

    public static List<string> Revise(Guid projectId, string request) =>
        ["revise", projectId.ToString(), request];

    public static List<string> Publish(
        Guid projectId, string target, IReadOnlyList<string> platforms,
        string? title, int? version, string? thumbnail, bool plain, bool dryRun)
    {
        var argv = new List<string>
        {
            "publish", projectId.ToString(), target,
            "--platforms", string.Join(',', platforms),
        };
        if (!string.IsNullOrWhiteSpace(title)) argv.AddRange(["--title", title]);
        if (version is { } pick) argv.AddRange(["--version", Invariant(pick)]);
        if (!string.IsNullOrWhiteSpace(thumbnail)) argv.AddRange(["--thumbnail", thumbnail]);
        if (plain) argv.Add("--plain");
        if (dryRun) argv.Add("--dry-run");
        return argv;
    }

    public static List<string> Reclip(
        Guid projectId, double startSeconds, double endSeconds, string? title, string? description)
    {
        var argv = new List<string>
        {
            "reclip", projectId.ToString(), Invariant(startSeconds), Invariant(endSeconds),
        };
        if (!string.IsNullOrWhiteSpace(title)) argv.AddRange(["--title", title]);
        if (!string.IsNullOrWhiteSpace(description)) argv.AddRange(["--description", description]);
        return argv;
    }

    public static List<string> Research(Guid projectId, string mode, string? focus)
    {
        var argv = new List<string> { "research", projectId.ToString(), "--mode", mode };
        if (!string.IsNullOrWhiteSpace(focus)) argv.AddRange(["--focus", focus]);
        return argv;
    }

    public static List<string> Thumbnails(Guid projectId, string? prompt, int? version, int? select)
    {
        var argv = new List<string> { "thumbnails", projectId.ToString() };
        if (!string.IsNullOrWhiteSpace(prompt)) argv.AddRange(["--prompt", prompt]);
        if (version is { } wanted) argv.AddRange(["--version", Invariant(wanted)]);
        if (select is { } pick) argv.AddRange(["--select", Invariant(pick)]);
        return argv;
    }

    public static List<string> Reformat(Guid projectId, string fileName, string format, bool captions)
    {
        var argv = new List<string> { "reformat", projectId.ToString(), fileName, "--format", format };
        if (captions) argv.Add("--captions");
        return argv;
    }

    public static List<string> Cleanup(int limit) => ["cleanup", "--limit", Invariant(limit)];

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
