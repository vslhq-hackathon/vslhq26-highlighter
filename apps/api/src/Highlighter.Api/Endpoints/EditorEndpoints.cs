using System.Security.Claims;
using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;
using Highlighter.Api.Infrastructure;
using Highlighter.Api.Services;
using static Highlighter.Api.Endpoints.ProjectEndpoints;

namespace Highlighter.Api.Endpoints;

/// <summary>The timeline editor's persistence + export surface. Clip documents
/// live on the clip row; long-form drafts live on the version row they edit.
/// Exports run as in-process jobs (same registry/logs as worker jobs).</summary>
public static class EditorEndpoints
{
    public static IEndpointConventionBuilder MapEditorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");

        // ---- clips ------------------------------------------------------- //

        group.MapGet("/{id:guid}/clips/{clipId:guid}/editor",
            async (Guid id, Guid clipId, ClaimsPrincipal user, SupabaseDb db, CancellationToken ct) =>
            {
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var row = await db.GetClipAsync(clipId, id, ct);
                if (row is null)
                    return Problem(404, "Clip not found", $"No clip {clipId} in project {id}");
                return Results.Ok(await ClipDocResponseAsync(db, id, row, ct));
            })
            .WithName("GetClipEditorDoc");

        group.MapPut("/{id:guid}/clips/{clipId:guid}/editor",
            async (Guid id, Guid clipId, SaveEditorRequest body, ClaimsPrincipal user,
                SupabaseDb db, CancellationToken ct) =>
            {
                if (body?.Doc is null)
                    return Problem(400, "Invalid request", "doc is required");
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var row = await db.GetClipAsync(clipId, id, ct);
                if (row is null)
                    return Problem(404, "Clip not found", $"No clip {clipId} in project {id}");
                var doc = EditorDocs.Normalize(body.Doc);
                if (EditorDocs.Validate(doc, EffectiveSourceDuration(row)) is { } invalid)
                    return Problem(400, "Invalid document", invalid);

                var saved = await new EditorStore(db).SaveClipDocAsync(id, clipId, doc, ct);
                if (saved is null)
                    return Problem(404, "Clip not found", $"No clip {clipId} in project {id}");
                return Results.Ok(await ClipDocResponseAsync(db, id, saved, ct));
            })
            .WithName("SaveClipEditorDoc");

        group.MapPost("/{id:guid}/clips/{clipId:guid}/editor/export",
            async (Guid id, Guid clipId, ExportEditorRequest? body, ClaimsPrincipal user,
                SupabaseDb db, EditorExportService exports, CancellationToken ct) =>
            {
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var row = await db.GetClipAsync(clipId, id, ct);
                if (row is null)
                    return Problem(404, "Clip not found", $"No clip {clipId} in project {id}");

                var doc = body?.Doc is null ? null : EditorDocs.Normalize(body.Doc);
                if (doc is not null)
                {
                    if (EditorDocs.Validate(doc, EffectiveSourceDuration(row)) is { } invalid)
                        return Problem(400, "Invalid document", invalid);
                    row = await new EditorStore(db).SaveClipDocAsync(id, clipId, doc, ct) ?? row;
                }
                doc ??= EditorStore.ClipDoc(row);
                if (doc is null)
                    return Problem(409, "Nothing to export",
                        "No saved editor document — save (or pass) one first");

                var clip = ProjectShaper.Clip(row);
                var master = EditorStore.ClipMaster(row);
                if (master is null
                    && clip.VerticalUrl is null && clip.VideoUrl is null && clip.CaptionedUrl is null)
                    return Problem(409, "Clip not exportable", "The clip has no rendered media yet");

                var job = exports.StartClipExport(id, clip, doc, master, AuthHelpers.Uid(user));
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("ExportClipEdit");

        // ---- long-form ---------------------------------------------------- //

        group.MapGet("/{id:guid}/longform/editor",
            async (Guid id, int? version, ClaimsPrincipal user, SupabaseDb db, CancellationToken ct) =>
            {
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var row = await db.GetLongformEditAsync(id, version, ct);
                if (row is null)
                    return Problem(404, "No long-form edit",
                        "This project has no long-form cut to edit");
                return Results.Ok(LongformDocResponse(row));
            })
            .WithName("GetLongformEditorDoc");

        group.MapPut("/{id:guid}/longform/editor",
            async (Guid id, int? version, SaveEditorRequest body, ClaimsPrincipal user,
                SupabaseDb db, CancellationToken ct) =>
            {
                if (body?.Doc is null)
                    return Problem(400, "Invalid request", "doc is required");
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var row = await db.GetLongformEditAsync(id, version, ct);
                if (row is null)
                    return Problem(404, "No long-form edit",
                        "This project has no long-form cut to edit");
                var doc = EditorDocs.Normalize(body.Doc);
                if (EditorDocs.Validate(doc, LongformDuration(row)) is { } invalid)
                    return Problem(400, "Invalid document", invalid);

                var saved = await new EditorStore(db).SaveLongformDraftAsync(row, doc, ct);
                return Results.Ok(LongformDocResponse(saved ?? row));
            })
            .WithName("SaveLongformEditorDoc");

        group.MapPost("/{id:guid}/longform/editor/export",
            async (Guid id, int? version, ExportEditorRequest? body, ClaimsPrincipal user,
                SupabaseDb db, EditorExportService exports, CancellationToken ct) =>
            {
                if (!await CanSeeAsync(db, id, AuthHelpers.Uid(user), ct)) return NotFound(id);
                var row = await db.GetLongformEditAsync(id, version, ct);
                if (row is null)
                    return Problem(404, "No long-form edit",
                        "This project has no long-form cut to edit");
                if (row["video_url"]?.GetValue<string>() is null)
                    return Problem(409, "Not exportable", "This version has no rendered video");

                var doc = body?.Doc is null ? null : EditorDocs.Normalize(body.Doc);
                if (doc is not null)
                {
                    if (EditorDocs.Validate(doc, LongformDuration(row)) is { } invalid)
                        return Problem(400, "Invalid document", invalid);
                    row = await new EditorStore(db).SaveLongformDraftAsync(row, doc, ct) ?? row;
                }
                doc ??= EditorStore.LongformDraft(row);
                if (doc is null)
                    return Problem(409, "Nothing to export",
                        "No saved editor document — save (or pass) one first");

                var job = exports.StartLongformExport(id, row, doc, AuthHelpers.Uid(user));
                return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
            })
            .WithName("ExportLongformEdit");

        return group;
    }

    private static double ClipDuration(JsonObject clipRow)
    {
        var start = Dbl(clipRow["start_seconds"]) ?? 0;
        var end = Dbl(clipRow["end_seconds"]) ?? 0;
        return Math.Max(0, end - start);
    }

    /// <summary>What the doc's source seconds run against: the padded master's
    /// duration when one exists (its handles are legal timeline material), else
    /// the bare clip length.</summary>
    private static double EffectiveSourceDuration(JsonObject clipRow) =>
        EditorStore.ClipMaster(clipRow)?.Duration ?? ClipDuration(clipRow);

    private static double LongformDuration(JsonObject editRow) =>
        Dbl(editRow["duration_seconds"]) ?? 0;

    /// <summary>PostgREST can serialize numerics as numbers or strings depending
    /// on column type — read either without throwing.</summary>
    private static double? Dbl(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return node.GetValue<double>();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return double.TryParse(node.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
    }

    private static async Task<EditorDocResponse> ClipDocResponseAsync(
        SupabaseDb db, Guid projectId, JsonObject row, CancellationToken ct)
    {
        var clip = ProjectShaper.Clip(row);
        var master = EditorStore.ClipMaster(row);
        var doc = EditorStore.ClipDoc(row);
        if (doc is null)
        {
            // Fresh document: the original cut on the timeline, captions seeded
            // from transcript words over the whole media window (handle words
            // included — they surface when the user extends into them). Times
            // are re-based so t=0 is the media file's first frame.
            var chunks = await db.ListTranscriptChunksAsync(projectId, includeWords: true, ct);
            var captions = EditorDocs.SeedCaptions(
                chunks.OfType<JsonObject>(),
                clip.StartSeconds - (master?.PadStart ?? 0),
                clip.EndSeconds + (master?.PadEnd ?? 0));
            doc = master is null
                ? EditorDocs.Default(clip.DurationSeconds, captions)
                : EditorDocs.Default(master.Duration, captions,
                    cutStart: master.PadStart,
                    cutEnd: master.PadStart + clip.DurationSeconds);
        }
        return new EditorDocResponse(
            doc,
            Target: "clip",
            ClipId: clip.Id,
            LongformVersion: null,
            SourceUrl: master?.Url ?? clip.VerticalUrl ?? clip.VideoUrl ?? clip.CaptionedUrl,
            PosterUrl: clip.ThumbnailUrl,
            SourceDuration: master?.Duration ?? clip.DurationSeconds,
            SavedAt: EditorStore.ClipSavedAt(row),
            Export: EditorStore.ClipExport(row),
            SourceFps: EditorStore.ClipFps(row),
            Handles: master is null ? null : new EditorHandlesDto(master.PadStart, master.PadEnd));
    }

    private static EditorDocResponse LongformDocResponse(JsonObject row)
    {
        var edit = ProjectShaper.Longform(row);
        var doc = EditorStore.LongformDraft(row)
            ?? EditorDocs.Default(edit.DurationSeconds ?? 0, []);
        return new EditorDocResponse(
            doc,
            Target: "longform",
            ClipId: null,
            LongformVersion: edit.Version,
            SourceUrl: edit.VideoUrl,
            PosterUrl: edit.ThumbnailUrl,
            SourceDuration: edit.DurationSeconds ?? 0,
            SavedAt: EditorStore.LongformSavedAt(row),
            Export: null);
    }
}
