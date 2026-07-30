using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;
using Highlighter.Api.Infrastructure;

namespace Highlighter.Api.Services;

/// <summary>Spawns and supervises worker processes. The registry is in-memory by
/// design: project rows stay authoritative, cooperative cancel survives an API
/// restart (the worker polls the DB), and the per-job log files are the durable
/// record. One active job per project; cleanup drains are globally single-flight.</summary>
public sealed class PipelineJobService(RepoLayout layout, SupabaseDb db, ILogger<PipelineJobService> log)
{
    private const int MaxTerminalJobs = 200;
    // A single demo machine can't survive unbounded parallel ffmpeg/yt-dlp work.
    private const int MaxConcurrentJobs = 6;
    private static readonly TimeSpan ForceKillGrace = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly Dictionary<string, PipelineJob> _jobs = [];
    private readonly List<PipelineJob> _order = [];

    public static string NewJobId() => "job_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Wait (bounded) for a job to reach a terminal state — for verbs
    /// that are metadata flips rather than renders, where the caller wants a
    /// definite answer. True when the job finished within the timeout.</summary>
    public static async Task<bool> WaitForExitAsync(
        PipelineJob job, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (job.State is JobState.Starting or JobState.Running
               && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250, ct);
        }
        return job.State is not (JobState.Starting or JobState.Running);
    }

    /// <summary>id may be preassigned (POST /api/projects writes it into the row's
    /// instance_id before the row exists, so it must be known up front).</summary>
    public PipelineJob Start(string kind, Guid? projectId, IReadOnlyList<string> workerArgs,
        string? id = null, Guid? ownerId = null, AgentChatRef? agentChat = null)
    {
        var command = layout.ResolveWorkerCommand()
            ?? throw new WorkerUnavailableException(
                "Pipeline worker binary not found: run `dotnet build pipeline-dotnet` "
                + "or set Pipeline:WorkerCommand.");

        id ??= NewJobId();
        var display = new List<string> { command.FileName };
        display.AddRange(command.PrefixArgs);
        display.AddRange(workerArgs);
        var logName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{kind}"
            + $"-{(projectId is { } p ? p.ToString()[..8] : "global")}-{id}.log";
        var job = new PipelineJob(id, kind, projectId, display,
            Path.Combine(layout.JobLogRoot, logName), ownerId)
        {
            AgentChat = agentChat,
        };

        lock (_gate)
        {
            if (projectId is { } pid && ActiveForProjectLocked(pid) is { } blocker)
                throw new JobConflictException(
                    $"Project {pid} already has an active job ({blocker.Id}: {blocker.Kind}).");
            if (kind == "cleanup" && _order.Any(other => other.Kind == "cleanup" && !other.IsTerminal))
                throw new JobConflictException("A cleanup drain is already running.");
            EnsureCapacityLocked();
            _jobs[id] = job;
            _order.Add(job);
        }

        try
        {
            Launch(job, command, workerArgs);
            log.LogInformation("Job {JobId} ({Kind}) started for project {ProjectId}: {Command}",
                id, kind, projectId, string.Join(' ', display));
        }
        catch (Exception exception)
        {
            log.LogError(exception, "Job {JobId} ({Kind}): worker spawn failed", id, kind);
            job.MarkFailed($"spawn failed: {exception.Message}");
            throw new WorkerUnavailableException(
                $"Failed to launch the pipeline worker: {exception.Message} (job {id}, log: {job.LogPath})");
        }
        return job;
    }

    /// <summary>An API-native job (editor exports): same registry, conflict
    /// rules, and durable log file as worker jobs, but the work runs in-process.
    /// The body returns an exit code; an exception marks the job failed.</summary>
    public PipelineJob StartExternal(string kind, Guid? projectId,
        IReadOnlyList<string> displayArgv, Func<PipelineJob, CancellationToken, Task<int>> body,
        Guid? ownerId = null)
    {
        var id = NewJobId();
        var logName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{kind}"
            + $"-{(projectId is { } p ? p.ToString()[..8] : "global")}-{id}.log";
        var job = new PipelineJob(id, kind, projectId, displayArgv,
            Path.Combine(layout.JobLogRoot, logName), ownerId);
        // In-process jobs have no OS process; force-cancel cancels this token.
        var cts = new CancellationTokenSource();
        job.ExternalCts = cts;

        lock (_gate)
        {
            if (projectId is { } pid && ActiveForProjectLocked(pid) is { } blocker)
                throw new JobConflictException(
                    $"Project {pid} already has an active job ({blocker.Id}: {blocker.Kind}).");
            EnsureCapacityLocked();
            _jobs[id] = job;
            _order.Add(job);
        }

        job.OpenSink(
        [
            $"job {job.Id} kind={kind}" + (projectId is { } proj ? $" project={proj}" : ""),
            $"command: {string.Join(' ', displayArgv)} (in-process)",
        ]);
        job.MarkRunning();
        log.LogInformation("Job {JobId} ({Kind}) started in-process for project {ProjectId}",
            id, kind, projectId);

        _ = Task.Run(async () =>
        {
            try
            {
                var exitCode = await body(job, cts.Token);
                job.MarkExited(exitCode, null);
                log.LogInformation("Job {JobId} ({Kind}) finished: {State} (exit {Exit})",
                    id, kind, job.State, exitCode);
            }
            catch (OperationCanceledException) when (job.KillRequested)
            {
                job.Append("api", "job cancelled by force-cancel");
                job.MarkExited(-1, null); // KillRequested makes this land as 'killed'
                log.LogInformation("Job {JobId} ({Kind}) cancelled by force-cancel", id, kind);
            }
            catch (Exception exception)
            {
                job.Append("err", exception.ToString());
                job.MarkFailed($"{kind} failed: {exception.Message}");
                log.LogError(exception, "Job {JobId} ({Kind}) failed; log: {LogPath}",
                    id, kind, job.LogPath);
            }
            finally
            {
                cts.Dispose();
                Evict();
            }
        });
        return job;
    }

    public PipelineJob? Get(string id)
    {
        lock (_gate) return _jobs.GetValueOrDefault(id);
    }

    public List<JobDto> List(string? state, Guid? projectId, Guid? userId = null)
    {
        List<PipelineJob> jobs;
        lock (_gate) jobs = _order.AsEnumerable().Reverse().ToList();
        return jobs
            .Where(job => job.VisibleTo(userId))
            .Where(job => projectId is null || job.ProjectId == projectId)
            .Select(job => job.ToDto())
            .Where(dto => state is null || dto.State == state)
            .ToList();
    }

    public PipelineJob? ActiveForProject(Guid projectId)
    {
        lock (_gate) return ActiveForProjectLocked(projectId);
    }

    public string? ActiveJobIdForProject(Guid projectId) => ActiveForProject(projectId)?.Id;

    /// <summary>Escalating stop for a registry-tracked process: SIGTERM (via
    /// /bin/kill — Process.Kill() is SIGKILL on Unix and would rob the worker of
    /// its own terminal status write), a grace period, then SIGKILL. Returns false
    /// when there is no live process to signal.</summary>
    public async Task<bool> ForceKillAsync(PipelineJob job)
    {
        job.RequestKill();
        Process? process;
        CancellationTokenSource? externalCts;
        lock (_gate)
        {
            process = job.Process;
            externalCts = job.ExternalCts;
        }
        if (process is null)
        {
            // In-process job (editor export): cancel its token — the renderer
            // kills its ffmpeg child on cancellation.
            if (externalCts is null || job.IsTerminal) return false;
            job.Append("api", "force-cancel: cancelling in-process job");
            try
            {
                externalCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            return true;
        }
        try
        {
            if (process.HasExited) return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        job.Append("api", "force-cancel: sending SIGTERM");
        try
        {
            var term = new Process
            {
                StartInfo = new ProcessStartInfo("/bin/kill") { UseShellExecute = false },
            };
            term.StartInfo.ArgumentList.Add("-TERM");
            term.StartInfo.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
            term.Start();
            await term.WaitForExitAsync();
            term.Dispose();
        }
        catch (Exception exception)
        {
            log.LogWarning("Job {JobId}: SIGTERM delivery failed: {Message}", job.Id, exception.Message);
        }

        using var grace = new CancellationTokenSource(ForceKillGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
        }

        job.Append("api", $"force-cancel: still alive after {ForceKillGrace.TotalSeconds:0}s, sending SIGKILL");
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
        }
        return true;
    }

    /// <summary>Durable-log fallback for jobs the (in-memory) registry no longer
    /// knows — typically after an API restart. Finds the file by the job id baked
    /// into its name.</summary>
    public List<JobLogLineDto>? ReadLogFileTail(string jobId, int tail)
    {
        // The id comes straight off the route and lands in a filesystem glob —
        // accept only the exact shape NewJobId produces.
        if (!System.Text.RegularExpressions.Regex.IsMatch(jobId, "^job_[0-9a-f]{12}$"))
            return null;
        if (!Directory.Exists(layout.JobLogRoot)) return null;
        var path = Directory.EnumerateFiles(layout.JobLogRoot, $"*-{jobId}.log").FirstOrDefault();
        if (path is null) return null;
        // Stream the file instead of loading it whole — ingest logs get large.
        var window = new Queue<string>(tail);
        var total = 0L;
        foreach (var line in File.ReadLines(path))
        {
            total++;
            if (window.Count == tail) window.Dequeue();
            window.Enqueue(line);
        }
        var result = new List<JobLogLineDto>();
        var seq = total - window.Count;
        foreach (var line in window)
            result.Add(ParseLogLine(line, ++seq));
        return result;
    }

    /// <summary>Pure decision table for fixing the project row after an ingest
    /// worker is OBSERVED dead — the only situation where the API may write
    /// terminal statuses. Guards make the race with a concurrent writer harmless.</summary>
    public static (string[] Guard, string Status, string? Error)? DecideRowFixup(
        string? rowStatus, int exitCode, string jobId) => rowStatus switch
    {
        "created" => (["created"], "failed",
            $"worker exited before claiming the project (exit {exitCode}); see job {jobId} logs"),
        "ingesting" => (["ingesting"], "failed",
            $"worker terminated unexpectedly (exit {exitCode}); see job {jobId} logs"),
        "stopping" => (["stopping"], "cancelled", null),
        _ => null,
    };

    internal static JobLogLineDto ParseLogLine(string raw, long seq)
    {
        // Format: "2026-07-29T12:00:00.000Z [out] text"
        var firstSpace = raw.IndexOf(' ');
        if (firstSpace > 0
            && DateTimeOffset.TryParse(raw[..firstSpace], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var at))
        {
            var rest = raw[(firstSpace + 1)..];
            if (rest.StartsWith('[') && rest.IndexOf(']') is var close and > 0)
                return new JobLogLineDto(seq, at, rest[1..close],
                    rest.Length > close + 2 ? rest[(close + 2)..] : "");
        }
        return new JobLogLineDto(seq, default, "out", raw);
    }

    private PipelineJob? ActiveForProjectLocked(Guid projectId) =>
        _order.LastOrDefault(job => job.ProjectId == projectId && !job.IsTerminal);

    private void EnsureCapacityLocked()
    {
        if (_order.Count(job => !job.IsTerminal) >= MaxConcurrentJobs)
            throw new JobConflictException(
                $"The server is already running {MaxConcurrentJobs} jobs; "
                + "wait for one to finish and try again.");
    }

    private void Launch(PipelineJob job, WorkerCommand command, IReadOnlyList<string> workerArgs)
    {
        job.OpenSink(
        [
            $"job {job.Id} kind={job.Kind}" + (job.ProjectId is { } p ? $" project={p}" : ""),
            $"command: {string.Join(' ', job.Argv)}",
            $"workdir: {layout.RepoRoot}",
        ]);

        var info = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = layout.RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in command.PrefixArgs) info.ArgumentList.Add(argument);
        foreach (var argument in workerArgs) info.ArgumentList.Add(argument);

        var process = new Process { StartInfo = info };
        process.Start();
        job.Process = process;
        job.MarkRunning();
        job.Append("api", $"worker pid {process.Id}");
        var pumpOut = Task.Run(() => PumpAsync(job, process.StandardOutput, "out"));
        var pumpErr = Task.Run(() => PumpAsync(job, process.StandardError, "err"));
        _ = Task.Run(() => SuperviseAsync(job, process, pumpOut, pumpErr));
    }

    private static async Task PumpAsync(PipelineJob job, StreamReader reader, string stream)
    {
        while (await reader.ReadLineAsync() is { } line) job.Append(stream, line);
    }

    private async Task SuperviseAsync(PipelineJob job, Process process, Task pumpOut, Task pumpErr)
    {
        try
        {
            await Task.WhenAll(pumpOut, pumpErr);
            await process.WaitForExitAsync();
            var exitCode = process.ExitCode;

            string? note = null;
            if (job.Kind == "ingest" && job.ProjectId is { } projectId)
                note = await ReconcileIngestRowAsync(job, projectId, exitCode);

            job.MarkExited(exitCode, note);
            if (job.State == JobState.Failed)
                log.LogError(
                    "Job {JobId} ({Kind}) failed with exit {Exit}; log: {LogPath}. Last lines:\n{Tail}",
                    job.Id, job.Kind, exitCode, job.LogPath,
                    string.Join("\n", job.Tail(40).Select(line => $"[{line.Stream}] {line.Line}")));
            else
                log.LogInformation("Job {JobId} ({Kind}) finished: {State} (exit {Exit})",
                    job.Id, job.Kind, job.State, exitCode);
        }
        catch (Exception exception)
        {
            log.LogError(exception, "Job {JobId}: supervision failed", job.Id);
            job.MarkFailed($"supervision failed: {exception.Message}");
        }
        finally
        {
            process.Dispose();
            Evict();
        }
        await WriteAgentChatCompletionAsync(job);
    }

    /// <summary>The durable "I'm back" message for a chat-started job: appended
    /// to agent_messages when the job ends, whether or not any browser is open.
    /// Never throws — a chat write must not affect job supervision.</summary>
    private async Task WriteAgentChatCompletionAsync(PipelineJob job)
    {
        if (job.AgentChat is not { } chat) return;
        try
        {
            string text;
            if (job.State == JobState.Succeeded)
            {
                var latest = await db.GetLongformEditAsync(chat.ProjectId);
                text = latest is not null
                    ? RevisionCompletionText(latest)
                    : $"The revision finished (job {job.Id}), but I can't see the new version yet — "
                      + "refresh the project to check.";
            }
            else
            {
                var reason = job.FailureReason ?? "it stopped without a status";
                var tail = string.Join(" · ", job.Tail(2)
                    .Select(line => line.Line.Trim())
                    .Where(line => line.Length > 0));
                text = $"The revision didn't finish — {reason}."
                    + (tail.Length > 0 ? $" Last log lines: {tail}" : "");
            }

            await db.InsertAgentMessageAsync(new JsonObject
            {
                ["project_id"] = chat.ProjectId.ToString(),
                ["context"] = chat.Context,
                ["role"] = "agent",
                ["text"] = text,
                ["job_id"] = job.Id,
                ["job_final"] = true,
            });
            log.LogInformation("Job {JobId}: agent chat completion recorded", job.Id);
        }
        catch (Exception exception)
        {
            log.LogError(exception, "Job {JobId}: agent chat completion write failed", job.Id);
        }
    }

    /// <summary>The success message for a finished revision, from the newest
    /// longform_edits row — revision.notes is already the revise agent's own
    /// summary of what it changed, so no extra model call is needed.</summary>
    public static string RevisionCompletionText(JsonObject editRow)
    {
        var version = (int)(editRow["version"]?.GetValue<double>() ?? 0);
        var duration = editRow["duration_seconds"]?.GetValue<double>();
        var notes = (editRow["revision"] as JsonObject)?["notes"]?.GetValue<string>()?.Trim();
        if (notes is { Length: > 600 }) notes = notes[..600].TrimEnd() + "…";

        var length = duration is { } seconds
            ? $" ({TimeSpan.FromSeconds(seconds):m\\:ss})"
            : "";
        var text = $"Done — the revision rendered as v{version}{length}.";
        if (!string.IsNullOrEmpty(notes)) text += $" {notes}";
        return text + " Take a look and tell me what to adjust next.";
    }

    private async Task<string?> ReconcileIngestRowAsync(PipelineJob job, Guid projectId, int exitCode)
    {
        try
        {
            var status = await db.GetProjectStatusAsync(projectId);
            var fixup = DecideRowFixup(status, exitCode, job.Id);
            if (fixup is null) return $"row status after exit: {status ?? "row missing"}";

            var patch = new JsonObject { ["status"] = fixup.Value.Status };
            if (fixup.Value.Error is not null) patch["error"] = fixup.Value.Error;
            var updated = await db.PatchProjectGuardedAsync(projectId, fixup.Value.Guard, patch);
            log.LogWarning("Job {JobId}: reconciled project {ProjectId} row '{From}' -> '{To}' (applied: {Applied})",
                job.Id, projectId, status, fixup.Value.Status, updated is not null);
            return updated is not null
                ? $"row reconciled: {status} -> {fixup.Value.Status}"
                : $"row reconciliation guard missed (row moved past '{status}')";
        }
        catch (PostgrestException exception)
        {
            log.LogError("Job {JobId}: row reconciliation failed: {Message}", job.Id, exception.Message);
            return $"row reconciliation failed: {exception.Message}";
        }
    }

    private void Evict()
    {
        lock (_gate)
        {
            var terminal = _order.Where(job => job.IsTerminal).ToList();
            var excess = terminal.Count - MaxTerminalJobs;
            for (var i = 0; i < excess; i++)
            {
                _order.Remove(terminal[i]);
                _jobs.Remove(terminal[i].Id);
            }
        }
    }
}
