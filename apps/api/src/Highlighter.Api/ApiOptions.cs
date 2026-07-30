namespace Highlighter.Api;

/// <summary>Bound from the "Api" configuration section.</summary>
public sealed class ApiOptions
{
    /// <summary>When non-empty, /api/* requests must carry a matching X-Api-Key header.
    /// Empty (the default) disables the gate — the API binds to localhost.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>When true, /api/* (except /api/auth) requires a valid Supabase
    /// user JWT (Authorization: Bearer). Reads and writes are then scoped to the
    /// caller's own projects. Turn off for tokenless scripting/debugging.</summary>
    public bool RequireAuth { get; set; } = true;

    public string[] CorsOrigins { get; set; } = [];
}

/// <summary>Bound from the "Pipeline" configuration section.</summary>
public sealed class PipelineOptions
{
    /// <summary>Explicit worker launch command ("dotnet /path/highlighter.dll" or
    /// "/path/highlighter"). Empty → auto-resolve the built pipeline-dotnet CLI.</summary>
    public string WorkerCommand { get; set; } = "";

    /// <summary>Monorepo root override. Empty → walk up to the first directory
    /// containing .git or .env.</summary>
    public string RepoRoot { get; set; } = "";

    /// <summary>Directory for per-job worker logs. Empty → {repo}/outputs/api/jobs.</summary>
    public string JobLogRoot { get; set; } = "";

    /// <summary>When true, ingest jobs are dispatched to remote Container Apps
    /// Job workers via the Azure Storage queue (JOBS_QUEUE_CONNECTION /
    /// JOBS_QUEUE_NAME env vars) instead of a local subprocess, and all jobs are
    /// mirrored to the pipeline_jobs table so any API replica can answer job
    /// reads. False (the default) keeps the single-machine dev behavior.</summary>
    public bool DistributedIngest { get; set; }

    public int CleanupIntervalMinutes { get; set; } = 15;

    public int CleanupLimit { get; set; } = 50;
}
