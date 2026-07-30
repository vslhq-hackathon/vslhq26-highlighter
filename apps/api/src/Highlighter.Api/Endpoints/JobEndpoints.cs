using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using Highlighter.Api.Contracts;
using Highlighter.Api.Infrastructure;
using Highlighter.Api.Services;

namespace Highlighter.Api.Endpoints;

/// <summary>Job reads are scoped to the requesting user exactly like project
/// reads: someone else's job (its argv carries their prompts and sources, its
/// logs their transcripts) reads as nonexistent. The in-memory registry serves
/// this replica's jobs; the pipeline_jobs table and the shared log files cover
/// remote workers' jobs and other replicas' (or a restarted API's) jobs.</summary>
public static class JobEndpoints
{
    public static IEndpointConventionBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs");

        group.MapGet("/", async (ClaimsPrincipal user, PipelineJobService jobs, JobQueue queue,
                string? state, Guid? projectId, CancellationToken ct) =>
            {
                var uid = AuthHelpers.Uid(user);
                var local = jobs.List(state, projectId, uid);
                if (!queue.Enabled) return Results.Ok(local);
                // Registry entries win: they're live (log seq, sub-second state).
                var seen = local.Select(job => job.Id).ToHashSet();
                local.AddRange((await queue.ListAsync(state, projectId, uid, ct))
                    .Where(job => !seen.Contains(job.Id)));
                return Results.Ok(local
                    .OrderByDescending(job => job.StartedAt)
                    .ToList());
            })
            .WithName("ListJobs");

        group.MapGet("/{id}", async (string id, ClaimsPrincipal user, PipelineJobService jobs,
                JobQueue queue, CancellationToken ct) =>
            {
                if (jobs.Get(id) is { } tracked)
                    return Visible(tracked, user) is { } job ? Results.Ok(job.ToDto()) : NotFound(id);
                var remote = await queue.GetJobAsync(id, AuthHelpers.Uid(user), ct);
                return remote is null ? NotFound(id) : Results.Ok(remote);
            })
            .WithName("GetJob");

        group.MapGet("/{id}/logs", (string id, ClaimsPrincipal user, PipelineJobService jobs,
                int? tail) =>
            {
                var count = Math.Clamp(tail ?? 200, 1, 4000);
                if (jobs.Get(id) is { } tracked)
                    return Visible(tracked, user) is { } job
                        ? Results.Ok(job.Tail(count))
                        : NotFound(id);
                // Registry is in-memory; the durable file (shared across replicas
                // and written directly by remote workers) still serves.
                var fromFile = jobs.ReadLogFileTail(id, count);
                return fromFile is null ? NotFound(id) : Results.Ok(fromFile);
            })
            .WithName("GetJobLogs");

        group.MapGet("/{id}/logs/stream",
            async (string id, ClaimsPrincipal user, PipelineJobService jobs, JobQueue queue,
                int? tail, CancellationToken ct) =>
            {
                var count = Math.Clamp(tail ?? 200, 0, 4000);
                if (jobs.Get(id) is { } tracked)
                {
                    var job = Visible(tracked, user);
                    if (job is null) return NotFound(id);
                    return (IResult)TypedResults.ServerSentEvents(StreamAsync(job, count, ct));
                }
                // Not on this replica: follow the shared log file while the
                // pipeline_jobs row stays non-terminal.
                var remote = await queue.GetJobAsync(id, AuthHelpers.Uid(user), ct);
                if (remote is null) return NotFound(id);
                return TypedResults.ServerSentEvents(StreamFileAsync(queue, remote, count, ct));
            })
            .WithName("StreamJobLogs");

        return group;
    }

    private static PipelineJob? Visible(PipelineJob? job, ClaimsPrincipal user) =>
        job is not null && job.VisibleTo(AuthHelpers.Uid(user)) ? job : null;

    private static async IAsyncEnumerable<SseItem<object>> StreamAsync(
        PipelineJob job, int tail, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in job.StreamLogsAsync(tail, ct))
            yield return new SseItem<object>(line, "log")
            {
                EventId = line.Seq.ToString(CultureInfo.InvariantCulture),
            };
        // Terminal marker with the final job state, so clients know why the stream ended.
        yield return new SseItem<object>(job.ToDto(), "end");
    }

    private static bool IsTerminal(JobDto job) =>
        job.State is "succeeded" or "failed" or "killed";

    /// <summary>Tail-follow of the durable log file for a job some other process
    /// owns: replay the last <paramref name="tail"/> lines, then poll the file
    /// for appends (only consuming through the last complete line) until the
    /// pipeline_jobs row goes terminal and the file stops growing.</summary>
    private static async IAsyncEnumerable<SseItem<object>> StreamFileAsync(
        JobQueue queue, JobDto job, int tail, [EnumeratorCancellation] CancellationToken ct)
    {
        var path = job.LogPath;
        long offset = 0, seq = 0;
        var carry = new StringBuilder();
        var lastState = job;

        List<JobLogLineDto> ReadNewLines()
        {
            var lines = new List<JobLogLineDto>();
            if (path is null || !File.Exists(path)) return lines;
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= offset) return lines;
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var chunk = reader.ReadToEnd();
            offset = stream.Length;
            carry.Append(chunk);
            var text = carry.ToString();
            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0) return lines;
            carry.Clear();
            carry.Append(text[(lastNewline + 1)..]);
            foreach (var raw in text[..lastNewline].Split('\n'))
                lines.Add(PipelineJobService.ParseLogLine(raw.TrimEnd('\r'), ++seq));
            return lines;
        }

        // Initial replay: everything so far, trimmed to the requested tail.
        var initial = ReadNewLines();
        foreach (var line in initial.Skip(Math.Max(0, initial.Count - tail)))
            yield return new SseItem<object>(line, "log")
            {
                EventId = line.Seq.ToString(CultureInfo.InvariantCulture),
            };

        var quietPollsAfterTerminal = 0;
        var stateRefreshCountdown = 0;
        while (!ct.IsCancellationRequested)
        {
            var lines = ReadNewLines();
            foreach (var line in lines)
                yield return new SseItem<object>(line, "log")
                {
                    EventId = line.Seq.ToString(CultureInfo.InvariantCulture),
                };

            if (--stateRefreshCountdown <= 0)
            {
                lastState = await queue.GetJobAsync(job.Id, null, ct) ?? lastState;
                stateRefreshCountdown = 3;
            }
            if (IsTerminal(lastState))
            {
                // One extra quiet poll so the worker's final flush isn't cut off.
                if (lines.Count == 0 && ++quietPollsAfterTerminal >= 2) break;
            }
            await Task.Delay(1500, ct);
        }
        yield return new SseItem<object>(lastState, "end");
    }

    private static IResult NotFound(string id) =>
        Results.Problem(title: "Job not found", detail: $"No job with id {id}", statusCode: 404);
}
