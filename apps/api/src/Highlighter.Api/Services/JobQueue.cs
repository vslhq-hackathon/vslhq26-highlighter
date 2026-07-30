using System.Text.Json.Nodes;
using Azure.Storage.Queues;
using Highlighter.Api.Contracts;
using Highlighter.Api.Infrastructure;

namespace Highlighter.Api.Services;

/// <summary>Distributed job dispatch (Pipeline:DistributedIngest): ingest runs
/// on remote Container Apps Job workers signalled through an Azure Storage
/// queue, with the pipeline_jobs table as the durable state record. Also
/// mirrors locally-spawned jobs into that table (best-effort) so job-state
/// reads work from any API replica. Disabled, every method is a no-op and the
/// API behaves exactly as before.</summary>
public sealed class JobQueue
{
    private static readonly string[] ActiveStates = ["pending", "running", "cancel_requested"];

    private readonly RepoLayout _layout;
    private readonly SupabaseDb _db;
    private readonly ILogger<JobQueue> _log;
    private readonly QueueClient? _queue;

    public JobQueue(PipelineOptions options, RepoLayout layout, SupabaseDb db, ILogger<JobQueue> log)
    {
        _layout = layout;
        _db = db;
        _log = log;
        var connection = Environment.GetEnvironmentVariable("JOBS_QUEUE_CONNECTION");
        if (options.DistributedIngest && !string.IsNullOrWhiteSpace(connection) && db.IsConfigured)
            _queue = new QueueClient(connection,
                Environment.GetEnvironmentVariable("JOBS_QUEUE_NAME") ?? "pipeline-jobs",
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        else if (options.DistributedIngest)
            log.LogWarning("Pipeline:DistributedIngest is on but JOBS_QUEUE_CONNECTION or Supabase "
                + "is missing — falling back to local subprocess dispatch.");
    }

    public bool Enabled => _queue is not null;

    /// <summary>Row first, message second: a message for a missing row would be
    /// dropped by the worker, while a row without a message is just a job that
    /// never starts and can be cancelled. Throws WorkerUnavailableException so
    /// POST /api/projects reuses its existing failure path.</summary>
    public async Task StartIngestAsync(string jobId, Guid projectId,
        IReadOnlyList<string> argv, Guid? ownerId, CancellationToken ct = default)
    {
        var logName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-ingest-{projectId.ToString()[..8]}-{jobId}.log";
        try
        {
            await _db.InsertRowAsync("pipeline_jobs", new JsonObject
            {
                ["id"] = jobId,
                ["kind"] = "ingest",
                ["project_id"] = projectId.ToString(),
                ["owner_id"] = ownerId?.ToString(),
                ["argv"] = new JsonArray([.. argv.Select(a => JsonValue.Create(a))]),
                ["status"] = "pending",
                ["log_name"] = logName,
            }, ct);
            await _queue!.SendMessageAsync(new JsonObject
            {
                ["job_id"] = jobId,
                ["project_id"] = projectId.ToString(),
                ["kind"] = "ingest",
            }.ToJsonString(), cancellationToken: ct);
            _log.LogInformation("Job {JobId} (ingest) queued for project {ProjectId}", jobId, projectId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new WorkerUnavailableException(
                $"Failed to queue the pipeline job: {exception.Message} (job {jobId})");
        }
    }

    public async Task<JobDto?> GetJobAsync(string id, Guid? userId, CancellationToken ct = default)
    {
        if (!Enabled || !IsJobId(id)) return null;
        var rows = await _db.GetArrayAsync("pipeline_jobs", $"id=eq.{id}&select=*", ct);
        var dto = rows.Count > 0 ? ToDto(rows[0] as JsonObject) : null;
        return dto is not null && (userId is null || OwnerOf(rows[0] as JsonObject) == userId)
            ? dto
            : null;
    }

    public async Task<List<JobDto>> ListAsync(
        string? state, Guid? projectId, Guid? userId, CancellationToken ct = default)
    {
        if (!Enabled) return [];
        var query = "select=*&order=created_at.desc&limit=100"
            + (projectId is { } pid ? $"&project_id=eq.{pid}" : "")
            + (userId is { } uid ? $"&owner_id=eq.{uid}" : "");
        var rows = await _db.GetArrayAsync("pipeline_jobs", query, ct);
        return rows.OfType<JsonObject>()
            .Select(ToDto)
            .OfType<JobDto>()
            .Where(dto => state is null || dto.State == state)
            .ToList();
    }

    /// <summary>The newest non-terminal row for a project — the remote analogue
    /// of PipelineJobService.ActiveForProject.</summary>
    public async Task<JobDto?> ActiveForProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!Enabled) return null;
        var rows = await _db.GetArrayAsync("pipeline_jobs",
            $"project_id=eq.{projectId}&status=in.({string.Join(',', ActiveStates)})"
            + "&select=*&order=created_at.desc&limit=1", ct);
        return rows.Count > 0 ? ToDto(rows[0] as JsonObject) : null;
    }

    /// <summary>Flags the row cancel_requested; the worker wrapper polls it and
    /// SIGTERMs its child. A still-pending job is killed outright — the worker
    /// drops it when the message arrives. True when a row was flagged.</summary>
    public async Task<bool> RequestCancelAsync(string id, CancellationToken ct = default)
    {
        if (!Enabled || !IsJobId(id)) return false;
        var updated = await _db.PatchRowsAsync("pipeline_jobs",
            $"id=eq.{id}&status=in.(pending,running)&select=id",
            new JsonObject { ["status"] = "cancel_requested" }, ct);
        return updated.Count > 0;
    }

    // ---- local-job mirroring (fire-and-forget; never throws) ----

    public void MirrorStart(PipelineJob job)
    {
        if (!Enabled) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _db.InsertRowAsync("pipeline_jobs", new JsonObject
                {
                    ["id"] = job.Id,
                    ["kind"] = job.Kind,
                    ["project_id"] = job.ProjectId?.ToString(),
                    ["owner_id"] = job.OwnerId?.ToString(),
                    ["argv"] = new JsonArray([.. job.Argv.Select(a => JsonValue.Create(a))]),
                    ["status"] = "running",
                    ["log_name"] = Path.GetFileName(job.LogPath),
                    ["started_at"] = job.StartedAt.UtcDateTime.ToString("O"),
                    ["worker"] = Environment.MachineName,
                });
            }
            catch (Exception exception)
            {
                _log.LogWarning("Job {JobId}: pipeline_jobs mirror insert failed: {Message}",
                    job.Id, exception.Message);
            }
        });
    }

    public void MirrorEnd(PipelineJob job)
    {
        if (!Enabled) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _db.PatchRowsAsync("pipeline_jobs", $"id=eq.{job.Id}&select=id", new JsonObject
                {
                    ["status"] = job.State.ToString().ToLowerInvariant(),
                    ["exit_code"] = job.ExitCode,
                    ["error"] = job.FailureReason,
                    ["ended_at"] = (job.EndedAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("O"),
                });
            }
            catch (Exception exception)
            {
                _log.LogWarning("Job {JobId}: pipeline_jobs mirror update failed: {Message}",
                    job.Id, exception.Message);
            }
        });
    }

    // ---- mapping ----

    private static bool IsJobId(string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(id, "^job_[0-9a-f]{12}$");

    private static Guid? OwnerOf(JsonObject? row) =>
        Guid.TryParse(row?["owner_id"]?.GetValue<string>(), out var uid) ? uid : null;

    private JobDto? ToDto(JsonObject? row)
    {
        if (row?["id"]?.GetValue<string>() is not { } id) return null;
        var status = row["status"]?.GetValue<string>() ?? "pending";
        return new JobDto(
            id,
            row["kind"]?.GetValue<string>() ?? "ingest",
            Guid.TryParse(row["project_id"]?.GetValue<string>(), out var pid) ? pid : null,
            status switch
            {
                "pending" => "starting",
                "cancel_requested" => "running",
                _ => status,
            },
            (row["argv"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToList() ?? [],
            row["exit_code"] is JsonValue exit && exit.TryGetValue<int>(out var code) ? code : null,
            row["error"]?.GetValue<string>(),
            row["log_name"]?.GetValue<string>() is { } logName
                ? Path.Combine(_layout.JobLogRoot, logName)
                : null,
            ParseTime(row["started_at"]) ?? ParseTime(row["created_at"]) ?? default,
            ParseTime(row["ended_at"]),
            0);
    }

    private static DateTimeOffset? ParseTime(JsonNode? node) =>
        DateTimeOffset.TryParse(node?.GetValue<string>(), out var at) ? at : null;
}
