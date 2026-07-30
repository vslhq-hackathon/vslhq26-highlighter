using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Highlighter.Api.Contracts;
using Highlighter.Api.Infrastructure;
using Highlighter.Api.Services;

namespace Highlighter.Api.Endpoints;

public static class ProjectEndpoints
{
    private static readonly string[] ClipStatuses = ["detected", "rendered", "failed"];
    private static readonly Regex TargetMinutesPattern = new(@"^\d+(-\d+)?$", RegexOptions.Compiled);

    public static IEndpointConventionBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapPost("/",
            async (CreateProjectRequest request, ClaimsPrincipal user, SupabaseDb db,
                PipelineJobService jobs, JobQueue queue, SourceTitleService titles,
                ILogger<PipelineJobService> log, CancellationToken ct) =>
            {
                if (Validate(request) is { } invalid) return invalid;
                SourceDetector.TryDetect(request.SourceUrl!, out var source);

                var jobId = PipelineJobService.NewJobId();
                var row = new JsonObject
                {
                    ["name"] = string.IsNullOrWhiteSpace(request.Name) ? source.Name : request.Name.Trim(),
                    ["source_type"] = source.SourceType,
                    ["source_url"] = request.SourceUrl,
                    ["status"] = "created",
                    ["min_clip_score"] = request.MinClipScore ?? 0,
                    ["instance_id"] = jobId,
                    // The worker's metadata merge is a SHALLOW top-level overlay:
                    // "api" is the one key it never writes, so ours survives.
                    ["metadata"] = new JsonObject { ["api"] = ApiMetadata(jobId, request) },
                };
                if (AuthHelpers.Uid(user) is { } uid) row["user_id"] = uid.ToString();
                var inserted = await db.InsertProjectAsync(row, ct);
                var projectId = Guid.Parse(inserted["id"]!.GetValue<string>());

                try
                {
                    var argv = WorkerArgs.Ingest(projectId, request,
                        llmConcurrency: EnvInt("LLM_CONCURRENCY"),
                        transcribeConcurrency: EnvInt("TRANSCRIBE_CONCURRENCY"));
                    if (queue.Enabled)
                        await queue.StartIngestAsync(jobId, projectId, argv, AuthHelpers.Uid(user), ct);
                    else
                        jobs.Start("ingest", projectId, argv, jobId, AuthHelpers.Uid(user));
                }
                catch (WorkerUnavailableException exception)
                {
                    // Don't strand the row in 'created' — the queued project would look
                    // alive forever.
                    try
                    {
                        await db.PatchProjectGuardedAsync(projectId, ["created"], new JsonObject
                        {
                            ["status"] = "failed",
                            ["error"] = $"worker spawn failed: {exception.Message}",
                        }, ct: ct);
                    }
                    catch (PostgrestException patchFailure)
                    {
                        log.LogError("Could not mark project {ProjectId} failed after spawn failure: {Message}",
                            projectId, patchFailure.Message);
                    }
                    return Problem(502, "Pipeline worker unavailable",
                        $"{exception.Message} — project {projectId} was marked failed");
                }

                // No explicit name: swap in the source's real title once yt-dlp
                // reports it (background, guarded on the placeholder).
                if (string.IsNullOrWhiteSpace(request.Name)
                    && inserted["name"]?.GetValue<string>() is { Length: > 0 } placeholder)
                    titles.QueueRename(projectId, request.SourceUrl!, placeholder);

                return Results.Created($"/api/projects/{projectId}",
                    ProjectShaper.Summary(inserted, jobId));
            })
            .WithName("CreateProject");

        group.MapDelete("/{id:guid}",
            async (Guid id, bool? force, ClaimsPrincipal user, SupabaseDb db, PipelineJobService jobs,
                JobQueue queue, RepoLayout layout, MediaCleanupScheduler cleanup, SupabaseStorage storage,
                ILogger<PipelineJobService> log, CancellationToken ct) =>
            {
                var uid = AuthHelpers.Uid(user);
                if (!await CanSeeAsync(db, id, uid, ct)) return NotFound(id);

                var active = jobs.ActiveForProject(id);
                if (active is not null)
                {
                    if (force != true)
                        return Problem(409, "Project has an active job",
                            $"Job {active.Id} ({active.Kind}) is running; cancel it first or pass ?force=true");
                    await jobs.ForceKillAsync(active);
                }
                else if (await queue.ActiveForProjectAsync(id, ct) is { } remote)
                {
                    if (force != true)
                        return Problem(409, "Project has an active job",
                            $"Job {remote.Id} ({remote.Kind}) is running; cancel it first or pass ?force=true");
                    // Remote worker: flag its row; the wrapper SIGTERMs within its
                    // supervise period. The row delete below cascades this job row,
                    // which the wrapper tolerates.
                    await queue.RequestCancelAsync(remote.Id, ct);
                }

                if (!await db.DeleteProjectAsync(id, uid, ct)) return NotFound(id);

                // Storage objects the DB outbox triggers don't know about: editor
                // exports and thumbnail variants live under deterministic prefixes.
                // Best-effort: the row is already gone, so a sweep failure must
                // not turn a successful delete into an error response.
                try
                {
                    await storage.DeletePrefixAsync($"{id}/editor", ct);
                    await storage.DeletePrefixAsync($"{id}/thumbnails", ct);
                }
                catch (Exception exception)
                {
                    log.LogWarning("Post-delete storage sweep failed for {ProjectId}: {Message}",
                        id, exception.Message);
                }

                // The DB cascade + outbox triggers handle remote media; the local
                // mirror is ours to remove (path is pinned under the projects root).
                var mirror = layout.ProjectDir(id);
                if (mirror.StartsWith(layout.ProjectsRoot, StringComparison.Ordinal)
                    && Directory.Exists(mirror))
                {
                    try
                    {
                        Directory.Delete(mirror, recursive: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
                // The delete triggers just filled the outbox — drain it promptly.
                cleanup.RequestDrain();
                return Results.NoContent();
            })
            .WithName("DeleteProject");

        group.MapDelete("/{id:guid}/clips/{clipId:guid}",
            async (Guid id, Guid clipId, ClaimsPrincipal user, SupabaseDb db,
                PipelineJobService jobs, MediaCleanupScheduler cleanup, CancellationToken ct) =>
            {
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                if (jobs.ActiveForProject(id) is { } active)
                    return Problem(409, "Project has an active job",
                        $"Job {active.Id} ({active.Kind}) is running; try again when it finishes");
                if (!await db.DeleteClipAsync(clipId, id, ct))
                    return Problem(404, "Clip not found", $"No clip {clipId} in project {id}");
                // The clip's BEFORE DELETE trigger just enqueued its media.
                cleanup.RequestDrain();
                return Results.NoContent();
            })
            .WithName("DeleteClip");

        group.MapGet("/",
            async (ClaimsPrincipal user, SupabaseDb db, PipelineJobService jobs, JobQueue queue,
                int? limit, CancellationToken ct) =>
            {
                var rows = await db.ListProjectsAsync(
                    Math.Clamp(limit ?? 100, 1, 500), AuthHelpers.Uid(user), ct);
                return Results.Ok(rows.OfType<JsonObject>()
                    .Select(row => ProjectShaper.Summary(row, ActiveJobId(jobs, queue, row)))
                    .ToList());
            })
            .WithName("ListProjects");

        group.MapGet("/{id:guid}",
            async (Guid id, ClaimsPrincipal user, SupabaseDb db, RepoLayout layout,
                PipelineJobService jobs, JobQueue queue, CancellationToken ct) =>
            {
                var row = await db.GetProjectDetailAsync(id, AuthHelpers.Uid(user), ct);
                return row is null
                    ? NotFound(id)
                    : Results.Ok(ProjectShaper.Detail(
                        row, layout.HasLocalMirror(id), ActiveJobId(jobs, queue, row)));
            })
            .WithName("GetProject");

        group.MapGet("/{id:guid}/clips",
            async (Guid id, ClaimsPrincipal user, SupabaseDb db, string? pipeline, string? status,
                string? order, CancellationToken ct) =>
            {
                if (pipeline is not (null or "short" or "long"))
                    return Problem(400, "Invalid filter", "pipeline must be 'short' or 'long'");
                if (status is not null && !ClipStatuses.Contains(status))
                    return Problem(400, "Invalid filter", "status must be one of detected|rendered|failed");
                if (order is not (null or "score" or "start"))
                    return Problem(400, "Invalid filter", "order must be 'score' or 'start'");
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var rows = await db.ListClipsAsync(id, pipeline, status, order ?? "score", ct);
                return Results.Ok(rows.OfType<JsonObject>().Select(ProjectShaper.Clip).ToList());
            })
            .WithName("ListClips");

        group.MapGet("/{id:guid}/longform",
            async (Guid id, ClaimsPrincipal user, SupabaseDb db, CancellationToken ct) =>
            {
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var rows = await db.ListLongformEditsAsync(id, ct);
                return Results.Ok(rows.OfType<JsonObject>().Select(ProjectShaper.Longform).ToList());
            })
            .WithName("ListLongformEdits");

        group.MapGet("/{id:guid}/publications",
            async (Guid id, ClaimsPrincipal user, SupabaseDb db, CancellationToken ct) =>
            {
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var rows = await db.ListPublicationsAsync(id, ct);
                return Results.Ok(rows.OfType<JsonObject>().Select(ProjectShaper.Publication).ToList());
            })
            .WithName("ListPublications");

        group.MapGet("/{id:guid}/transcript",
            async (Guid id, ClaimsPrincipal user, SupabaseDb db, bool? includeWords, CancellationToken ct) =>
            {
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var withWords = includeWords ?? false;
                var rows = await db.ListTranscriptChunksAsync(id, withWords, ct);
                return Results.Ok(rows.OfType<JsonObject>()
                    .Select(row => ProjectShaper.TranscriptChunk(row, withWords))
                    .ToList());
            })
            .WithName("GetTranscript");

        return group;
    }

    /// <summary>Strict visibility: with a user id, a project someone else owns
    /// (or a legacy ownerless row) reads as nonexistent. Anonymous (auth off)
    /// sees everything, matching pre-auth behavior.</summary>
    internal static async Task<bool> CanSeeAsync(
        SupabaseDb db, Guid id, Guid? uid, CancellationToken ct) =>
        uid is null || await db.GetProjectAsync(id, "id", uid, ct) is not null;

    private static IResult? Validate(CreateProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceUrl))
            return Problem(400, "Invalid request", "sourceUrl is required");
        if (request.Pipeline is not ("short" or "long" or "both"))
            return Problem(400, "Invalid request", "pipeline must be one of short|long|both");
        if (!SourceDetector.TryDetect(request.SourceUrl, out _))
            return Problem(400, "Invalid request", SourceDetector.UnsupportedMessage(request.SourceUrl));
        if (request.MinClipScore is < 0 or > 1)
            return Problem(400, "Invalid request", "minClipScore must be between 0 and 1");
        if (request.TargetMinutes is { Length: > 0 } target && !TargetMinutesPattern.IsMatch(target))
            return Problem(400, "Invalid request", "targetMinutes must look like \"20\" or \"20-30\"");
        if (request.MaxChunks < 0)
            return Problem(400, "Invalid request", "maxChunks must be >= 0 (0 = unlimited)");
        if (request.ChunkSeconds is < 30 or > 600)
            return Problem(400, "Invalid request", "chunkSeconds must be between 30 and 600");
        return null;
    }

    private static JsonObject ApiMetadata(string jobId, CreateProjectRequest request) => new()
    {
        ["job_id"] = jobId,
        ["requested_at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.ffffff+00:00", System.Globalization.CultureInfo.InvariantCulture),
        ["client"] = "highlighter-api/1.0",
        ["request"] = new JsonObject
        {
            ["pipeline"] = request.Pipeline,
            ["instructions"] = request.Instructions,
            ["target_minutes"] = request.TargetMinutes,
            ["max_chunks"] = request.MaxChunks,
            ["chunk_seconds"] = request.ChunkSeconds,
            ["flags"] = new JsonObject
            {
                ["no_research"] = request.NoResearch,
                ["no_shots"] = request.NoShots,
                ["no_reframe"] = request.NoReframe,
                ["no_captions"] = request.NoCaptions,
                ["no_thumbnails"] = request.NoThumbnails,
                ["no_archive"] = request.NoArchive,
            },
        },
    };

    /// <summary>Registry first; with distributed dispatch the registry only knows
    /// this replica's jobs, so an active-looking row falls back to instance_id —
    /// the job id POST / pre-assigned before enqueueing.</summary>
    private static string? ActiveJobId(PipelineJobService jobs, JobQueue queue, JsonObject row)
    {
        if (Guid.TryParse(row["id"]?.GetValue<string>(), out var id)
            && jobs.ActiveJobIdForProject(id) is { } tracked)
            return tracked;
        return queue.Enabled
               && row["status"]?.GetValue<string>() is "created" or "ingesting" or "stopping"
            ? row["instance_id"]?.GetValue<string>()
            : null;
    }

    /// <summary>Concurrency knobs live in .env; passing them as explicit worker
    /// flags keeps API-triggered runs identical to manual CLI runs.</summary>
    private static int? EnvInt(string key) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var value) && value > 0
            ? value
            : null;

    internal static IResult NotFound(Guid id) =>
        Problem(404, "Project not found", $"No project with id {id}");

    internal static IResult Problem(int status, string title, string detail) =>
        Results.Problem(title: title, detail: detail, statusCode: status);
}
