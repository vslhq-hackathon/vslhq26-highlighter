using System.Security.Claims;
using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;
using Highlighter.Api.Infrastructure;
using Highlighter.Api.Services;
using static Highlighter.Api.Endpoints.ProjectEndpoints;

namespace Highlighter.Api.Endpoints;

/// <summary>Verbs on a project: cancel (guarded status write), revise, publish,
/// reclip (worker spawns). JobConflictException and WorkerUnavailableException
/// propagate to the global handler as 409/502 ProblemDetails.</summary>
public static class ProjectActionEndpoints
{
    private static readonly string[] AllowedPlatforms = ["tiktok", "instagram", "youtube", "x"];

    public static IEndpointConventionBuilder MapProjectActionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapPost("/{id:guid}/cancel",
            async (Guid id, bool? force, ClaimsPrincipal user, SupabaseDb db, PipelineJobService jobs,
                JobQueue queue, CancellationToken ct) =>
            {
                var uid = AuthHelpers.Uid(user);
                var job = jobs.ActiveForProject(id);

                // A remote worker owns this project's job: cooperative 'stopping'
                // write as usual, plus (on force) the cancel_requested flag its
                // wrapper polls — the remote analogue of ForceKillAsync.
                if (job is null && await queue.ActiveForProjectAsync(id, ct) is { } remote)
                {
                    var stopping = await db.PatchProjectGuardedAsync(id, ["created", "ingesting"],
                        new JsonObject { ["status"] = "stopping" }, uid, ct);
                    if (stopping is null && await db.GetProjectStatusAsync(id, uid, ct) is null)
                        return NotFound(id);
                    var flagged = force == true && await queue.RequestCancelAsync(remote.Id, ct);
                    var remoteStatus = await db.GetProjectStatusAsync(id, uid, ct) ?? "stopping";
                    return Results.Accepted($"/api/projects/{id}",
                        new CancelResultDto(id, remoteStatus, remote.Id, flagged));
                }

                // Force-cancel with no tracked process: either the worker is an
                // orphan of a previous API instance (it still polls the row and
                // stops on 'cancelled') or it's dead and the row is stranded —
                // writing 'cancelled' directly converges both. Decided BEFORE any
                // status write so a 409 can never leave the row half-moved.
                if (force == true && job is null)
                {
                    var current = await db.GetProjectStatusAsync(id, uid, ct);
                    if (current is null) return NotFound(id);
                    if (current is not ("created" or "ingesting" or "stopping"))
                        return Problem(409, "Cancellation not applicable",
                            $"Project is already '{current}'");
                    var recovered = await db.PatchProjectGuardedAsync(id,
                        ["created", "ingesting", "stopping"],
                        new JsonObject { ["status"] = "cancelled" }, uid, ct);
                    var recoveredStatus = recovered?["status"]?.GetValue<string>() ?? current;
                    return Results.Ok(new CancelResultDto(id, recoveredStatus, null, false));
                }

                // The cancel contract: write 'stopping' and let the worker converge to
                // 'cancelled' itself (it polls the row every ~10s). Never write
                // 'cancelled' while a worker may be alive.
                var patched = await db.PatchProjectGuardedAsync(id, ["created", "ingesting"],
                    new JsonObject { ["status"] = "stopping" }, uid, ct);
                if (patched is null)
                {
                    var status = await db.GetProjectStatusAsync(id, uid, ct);
                    if (status is null) return NotFound(id);
                    if (status is not "stopping")
                        return Problem(409, "Cancellation not applicable",
                            $"Project is already '{status}'");
                    // 'stopping' already — idempotent; force may still apply below.
                }

                var forceKilled = false;
                if (force == true && job is not null)
                    forceKilled = await jobs.ForceKillAsync(job);

                var finalStatus = await db.GetProjectStatusAsync(id, uid, ct) ?? "stopping";
                var result = new CancelResultDto(id, finalStatus, job?.Id, forceKilled);
                return patched is not null
                    ? Results.Accepted($"/api/projects/{id}", result)
                    : Results.Ok(result);
            })
            .WithName("CancelProject");

        group.MapPost("/{id:guid}/revise",
            async (Guid id, ReviseRequestDto body, ClaimsPrincipal user, SupabaseDb db,
                PipelineJobService jobs, RepoLayout layout, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.Request))
                    return Problem(400, "Invalid request", "request text is required");
                if (await db.GetProjectAsync(id, "id", AuthHelpers.Uid(user), ct) is null) return NotFound(id);
                if (!layout.HasLocalMirror(id)) return NoMirror(id, "revise");
                if (!await db.HasRenderedLongformAsync(id, ct))
                    return Problem(409, "Nothing to revise",
                        "No rendered long-form edit exists for this project");

                var job = jobs.Start("revise", id, WorkerArgs.Revise(id, body.Request.Trim()),
                    ownerId: AuthHelpers.Uid(user),
                    agentChat: body.FromChat ? new AgentChatRef(id, "long") : null);
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("ReviseProject");

        group.MapPost("/{id:guid}/publish",
            async (Guid id, PublishRequestDto body, ClaimsPrincipal user, SupabaseDb db,
                PipelineJobService jobs, RepoLayout layout, CancellationToken ct) =>
            {
                if (body.Target is not ("clip" or "longform"))
                    return Problem(400, "Invalid request", "target must be 'clip' or 'longform'");
                var platforms = (body.Platforms ?? [])
                    .Select(platform => platform.Trim().ToLowerInvariant())
                    .Where(platform => platform.Length > 0)
                    .Distinct()
                    .ToList();
                if (platforms.Count == 0)
                    return Problem(400, "Invalid request", "platforms is required");
                if (platforms.Except(AllowedPlatforms).FirstOrDefault() is { } unknown)
                    return Problem(400, "Invalid request",
                        $"unknown platform '{unknown}' (allowed: {string.Join(", ", AllowedPlatforms)})");
                if (platforms is ["x"])
                    return Problem(400, "Invalid request",
                        "'x' is a promo layer and requires a companion platform");

                if (await db.GetProjectAsync(id, "id", AuthHelpers.Uid(user), ct) is null) return NotFound(id);
                if (!layout.HasLocalMirror(id)) return NoMirror(id, "publish");

                string target;
                if (body.Target == "longform")
                {
                    target = "longform";
                }
                else
                {
                    if (body.ClipId is not { } clipId)
                        return Problem(400, "Invalid request", "clipId is required when target is 'clip'");
                    var clip = await db.GetClipAsync(clipId, id, ct);
                    if (clip is null)
                        return Problem(404, "Clip not found",
                            $"No clip {clipId} in project {id}");
                    var fileName = (((clip["metadata"] as JsonObject)?["render"]) as JsonObject)
                        ?["filename"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(fileName))
                        return Problem(409, "Clip not publishable",
                            "The clip has no rendered file on record");
                    target = fileName;
                }

                var job = jobs.Start("publish", id, WorkerArgs.Publish(
                    id, target, platforms, body.Title, body.Version, body.Thumbnail,
                    body.Plain, body.DryRun), ownerId: AuthHelpers.Uid(user));
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("PublishProject");

        group.MapPost("/{id:guid}/reclip",
            async (Guid id, ReclipRequestDto body, ClaimsPrincipal user, SupabaseDb db,
                PipelineJobService jobs, CancellationToken ct) =>
            {
                if (body.StartSeconds < 0 || body.EndSeconds <= body.StartSeconds)
                    return Problem(400, "Invalid request",
                        "startSeconds must be >= 0 and endSeconds greater than startSeconds");
                var project = await db.GetProjectAsync(id, "id,metadata", AuthHelpers.Uid(user), ct);
                if (project is null) return NotFound(id);
                if ((project["metadata"] as JsonObject)?["source_archive"] is null)
                    return Problem(409, "No source archive",
                        "reclip needs an archived livestream source (metadata.source_archive); "
                        + "VOD projects and --no-archive runs cannot be re-clipped");

                var job = jobs.Start("reclip", id, WorkerArgs.Reclip(
                    id, body.StartSeconds, body.EndSeconds, body.Title, body.Description),
                    ownerId: AuthHelpers.Uid(user));
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("ReclipProject");

        group.MapPost("/{id:guid}/research",
            async (Guid id, ResearchRequestDto? body, ClaimsPrincipal user, SupabaseDb db,
                PipelineJobService jobs, RepoLayout layout, CancellationToken ct) =>
            {
                var mode = string.IsNullOrWhiteSpace(body?.Mode) ? "long" : body.Mode.Trim();
                if (mode is not ("short" or "long"))
                    return Problem(400, "Invalid request", "mode must be 'short' or 'long'");
                if (await db.GetProjectAsync(id, "id", AuthHelpers.Uid(user), ct) is null) return NotFound(id);
                if (!layout.HasLocalMirror(id)) return NoMirror(id, "research");

                var job = jobs.Start("research", id, WorkerArgs.Research(id, mode, body?.Focus),
                    ownerId: AuthHelpers.Uid(user));
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("RerunResearch");

        group.MapPost("/{id:guid}/thumbnails",
            async (Guid id, ThumbnailsRequestDto? body, ClaimsPrincipal user, SupabaseDb db,
                PipelineJobService jobs, RepoLayout layout, CancellationToken ct) =>
            {
                if (await db.GetProjectAsync(id, "id", AuthHelpers.Uid(user), ct) is null) return NotFound(id);
                if (!layout.HasLocalMirror(id)) return NoMirror(id, "thumbnails");
                if (!await db.HasRenderedLongformAsync(id, ct))
                    return Problem(409, "Nothing to thumbnail",
                        "No rendered long-form edit exists for this project");

                var job = jobs.Start("thumbnails", id,
                    WorkerArgs.Thumbnails(id, body?.Prompt, body?.Version, select: null),
                    ownerId: AuthHelpers.Uid(user));
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("GenerateThumbnails");

        group.MapPost("/{id:guid}/thumbnails/select",
            async (Guid id, ThumbnailSelectRequestDto body, ClaimsPrincipal user, SupabaseDb db,
                PipelineJobService jobs, RepoLayout layout, CancellationToken ct) =>
            {
                if (body.Index < 1)
                    return Problem(400, "Invalid request", "index must be >= 1");
                if (await db.GetProjectAsync(id, "id", AuthHelpers.Uid(user), ct) is null) return NotFound(id);
                if (!layout.HasLocalMirror(id)) return NoMirror(id, "thumbnails");

                // Selection is a metadata flip, not a render — run it and wait
                // briefly so callers (the studio agent) get a definite answer.
                var job = jobs.Start("thumbnails", id,
                    WorkerArgs.Thumbnails(id, prompt: null, body.Version, select: body.Index),
                    ownerId: AuthHelpers.Uid(user));
                var finished = await PipelineJobService.WaitForExitAsync(
                    job, TimeSpan.FromSeconds(20), ct);
                return finished
                    ? Results.Ok(job.ToDto())
                    : Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("SelectThumbnail");

        group.MapPost("/{id:guid}/thumbnails/import",
            async (Guid id, ThumbnailImportRequestDto body, ClaimsPrincipal user, SupabaseDb db,
                SupabaseStorage storage, PipelineJobService jobs, RepoLayout layout,
                CancellationToken ct) =>
            {
                var extension = Path.GetExtension(body.FileName ?? "").ToLowerInvariant();
                if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
                    return Problem(400, "Invalid request",
                        "fileName must end in .png, .jpg, .jpeg or .webp");
                byte[] content;
                try
                {
                    content = Convert.FromBase64String(body.ContentBase64 ?? "");
                }
                catch (FormatException)
                {
                    return Problem(400, "Invalid request", "contentBase64 is not valid base64");
                }
                if (content.Length is 0 or > 8 * 1024 * 1024)
                    return Problem(400, "Invalid request", "image must be between 1 byte and 8 MB");
                if (await db.GetProjectAsync(id, "id", AuthHelpers.Uid(user), ct) is null) return NotFound(id);
                if (jobs.ActiveForProject(id) is { } active)
                    return Problem(409, "Project has an active job",
                        $"Job {active.Id} ({active.Kind}) is running; try again when it finishes");

                var row = await db.GetLongformEditAsync(id, body.Version, ct);
                if (row is null)
                    return Problem(409, "Nothing to thumbnail",
                        "No long-form edit exists for this project");
                var editId = Guid.Parse(row["id"]!.GetValue<string>());
                var version = (int)(row["version"]?.GetValue<double>() ?? 1);

                var metadata = row["metadata"] as JsonObject ?? new JsonObject();
                var render = metadata["render"] as JsonObject ?? new JsonObject();
                var thumbnails = render["thumbnails"] as JsonObject
                    ?? new JsonObject { ["variants"] = new JsonArray(), ["selected_index"] = 0 };
                var variants = thumbnails["variants"] as JsonArray ?? new JsonArray();
                var nextIndex = variants.OfType<JsonObject>()
                    .Select(variant => (int)(variant["index"]?.GetValue<double>() ?? 0))
                    .DefaultIfEmpty(0).Max() + 1;

                var storagePath = $"{id}/thumbnails/import_{nextIndex}{extension}";
                var contentType = extension is ".png" ? "image/png"
                    : extension is ".webp" ? "image/webp" : "image/jpeg";
                await storage.UploadAsync(storagePath, content, contentType, ct);
                var url = storage.PublicUrl(storagePath);

                variants.Add(new JsonObject
                {
                    ["index"] = nextIndex,
                    ["direction"] = "Imported",
                    ["overlay_text"] = "",
                    ["url"] = url,
                    ["storage_path"] = storagePath,
                });
                thumbnails["variants"] = variants;
                thumbnails["selected_index"] = variants.Count - 1;
                render["thumbnails"] = thumbnails;
                metadata["render"] = render;

                var patched = await db.PatchLongformEditAsync(editId, new JsonObject
                {
                    ["metadata"] = metadata.DeepClone(),
                    ["thumbnail_url"] = url,
                }, ct);
                // Keep the worker's mirror in step so `thumbnails --select` still
                // sees the imported variant (best-effort; row is authoritative).
                MirrorStore.UpdateLongformRow(layout.ProjectDir(id), version, mirrorRow =>
                {
                    var m = mirrorRow["metadata"] as JsonObject ?? new JsonObject();
                    m["render"] = render.DeepClone();
                    mirrorRow["metadata"] = m;
                    mirrorRow["thumbnail_url"] = url;
                });
                return Results.Ok(ProjectShaper.Longform(patched ?? row));
            })
            .WithName("ImportThumbnail");

        group.MapPost("/{id:guid}/clips/{clipId:guid}/reformat",
            async (Guid id, Guid clipId, ReformatRequestDto? body, ClaimsPrincipal user,
                SupabaseDb db, PipelineJobService jobs, RepoLayout layout, CancellationToken ct) =>
            {
                var format = string.IsNullOrWhiteSpace(body?.Format) ? "square" : body.Format.Trim();
                if (format is not "square")
                    return Problem(400, "Invalid request", "format must be 'square'");
                if (await db.GetProjectAsync(id, "id", AuthHelpers.Uid(user), ct) is null) return NotFound(id);
                if (!layout.HasLocalMirror(id)) return NoMirror(id, "reformat");

                var clip = await db.GetClipAsync(clipId, id, ct);
                if (clip is null)
                    return Problem(404, "Clip not found", $"No clip {clipId} in project {id}");
                var fileName = (((clip["metadata"] as JsonObject)?["render"]) as JsonObject)
                    ?["filename"]?.GetValue<string>();
                if (string.IsNullOrEmpty(fileName))
                    return Problem(409, "Clip not reformatable",
                        "The clip has no rendered file on record");

                var job = jobs.Start("reformat", id,
                    WorkerArgs.Reformat(id, fileName, format, body?.Captions ?? false),
                    ownerId: AuthHelpers.Uid(user));
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("ReformatClip");

        return group;
    }

    private static IResult NoMirror(Guid id, string verb) =>
        Problem(409, "No local mirror",
            $"{verb} reads the local run mirror (outputs/projects/{id}/project.json), which is "
            + "not present on this machine — it exists wherever ingest ran");
}
