using System.Security.Claims;
using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;
using Highlighter.Api.Infrastructure;
using Highlighter.Api.Services;
using static Highlighter.Api.Endpoints.ProjectEndpoints;

namespace Highlighter.Api.Endpoints;

/// <summary>Durable studio-agent chat: the web app loads and appends the
/// transcript here, and chat-started jobs write their completion rows
/// server-side (PipelineJobService), so the outcome is waiting even when the
/// browser left mid-job.</summary>
public static class AgentChatEndpoints
{
    private const int MaxTextLength = 8 * 1024;

    public static IEndpointConventionBuilder MapAgentChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapGet("/{id:guid}/agent/messages",
            async (Guid id, string? context, ClaimsPrincipal user, SupabaseDb db,
                PipelineJobService jobs, CancellationToken ct) =>
            {
                var chatContext = string.IsNullOrWhiteSpace(context) ? "long" : context.Trim();
                if (chatContext is not ("long" or "short"))
                    return Problem(400, "Invalid request", "context must be 'long' or 'short'");
                if (await db.GetProjectAsync(id, "id", AuthHelpers.Uid(user), ct) is null)
                    return NotFound(id);

                var rows = await db.ListAgentMessagesAsync(id, chatContext, ct: ct);
                // API-restart gap: a chat-started job whose in-memory registry
                // entry is gone can still complete (the worker outlives the API).
                // When its longform_edits row has landed, synthesize the
                // completion message the supervisor never got to write.
                var synthesized = await SynthesizeMissedCompletionAsync(id, chatContext, rows, db, jobs, ct);
                if (synthesized is not null) rows.Add(synthesized);
                return Results.Ok(rows.OfType<JsonObject>().Select(Shape).ToList());
            })
            .WithName("ListAgentMessages");

        group.MapPost("/{id:guid}/agent/messages",
            async (Guid id, AgentMessageCreateDto body, string? context, ClaimsPrincipal user,
                SupabaseDb db, CancellationToken ct) =>
            {
                var chatContext = string.IsNullOrWhiteSpace(context) ? "long" : context.Trim();
                if (chatContext is not ("long" or "short"))
                    return Problem(400, "Invalid request", "context must be 'long' or 'short'");
                if (body.Role is not ("user" or "agent"))
                    return Problem(400, "Invalid request", "role must be 'user' or 'agent'");
                if (string.IsNullOrWhiteSpace(body.Text))
                    return Problem(400, "Invalid request", "text is required");
                if (body.Text.Length > MaxTextLength)
                    return Problem(400, "Invalid request", $"text must be under {MaxTextLength} characters");
                if (await db.GetProjectAsync(id, "id", AuthHelpers.Uid(user), ct) is null)
                    return NotFound(id);

                var row = await db.InsertAgentMessageAsync(new JsonObject
                {
                    ["project_id"] = id.ToString(),
                    ["context"] = chatContext,
                    ["role"] = body.Role,
                    ["text"] = body.Text,
                    ["job_id"] = body.JobId,
                }, ct);
                return Results.Ok(Shape(row));
            })
            .WithName("AppendAgentMessage");

        return group;
    }

    private static async Task<JsonObject?> SynthesizeMissedCompletionAsync(
        Guid projectId, string context, JsonArray rows, SupabaseDb db, PipelineJobService jobs,
        CancellationToken ct)
    {
        var messages = rows.OfType<JsonObject>().ToList();
        var pending = messages.LastOrDefault(row =>
            row["job_id"]?.GetValue<string>() is { Length: > 0 });
        if (pending is null) return null;
        var jobId = pending["job_id"]!.GetValue<string>();
        if (messages.Any(row => row["job_id"]?.GetValue<string>() == jobId
                && row["job_final"]?.GetValue<bool>() == true))
            return null;
        // The registry still knows the job: the supervisor will write the
        // completion itself when it ends.
        if (jobs.Get(jobId) is not null) return null;

        // A new version created after the pending message means the orphaned
        // worker finished. No newer version: it may still be running — leave
        // the chat pending rather than guess a failure.
        var latest = await db.GetLongformEditAsync(projectId, version: null, ct);
        if (latest is null) return null;
        if (!DateTimeOffset.TryParse(pending["created_at"]?.GetValue<string>(), out var askedAt)
            || !DateTimeOffset.TryParse(latest["created_at"]?.GetValue<string>(), out var editAt)
            || editAt <= askedAt)
            return null;

        return await db.InsertAgentMessageAsync(new JsonObject
        {
            ["project_id"] = projectId.ToString(),
            ["context"] = context,
            ["role"] = "agent",
            ["text"] = PipelineJobService.RevisionCompletionText(latest),
            ["job_id"] = jobId,
            ["job_final"] = true,
        }, ct);
    }

    private static AgentMessageDto Shape(JsonObject row) => new(
        Guid.Parse(row["id"]!.GetValue<string>()),
        row["role"]?.GetValue<string>() ?? "agent",
        row["text"]?.GetValue<string>() ?? "",
        row["job_id"]?.GetValue<string>(),
        row["job_final"]?.GetValue<bool>() ?? false,
        DateTimeOffset.TryParse(row["created_at"]?.GetValue<string>(), out var at)
            ? at
            : DateTimeOffset.UtcNow);
}
