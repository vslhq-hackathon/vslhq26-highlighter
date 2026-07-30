using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Highlighter.Web.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Highlighter.Web.Services;

/// <summary>The editing agent in the studio's right panel: a Microsoft Agent
/// Framework agent running inside the Blazor app, whose tools are the
/// IStudioBackend surface. Capabilities differ by context — the long-form cut
/// gets the revision loop and thumbnails; a highlight clip gets research,
/// reformatting, and honest explanations of what it cannot do.</summary>
public class StudioAgentService(StudioState state, IStudioBackend backend, ApiClient api)
{
    private const int MaxReplayMessages = 40;

    private ChatClientAgent? _agent;
    private AgentSession? _session;
    private string? _sessionContext;
    private int _providerIndex;

    public async Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || state.AgentBusy) return;
        var trimmed = text.Trim();
        // Snapshot BEFORE the optimistic append: a fresh model session replays
        // this as prior turns while the new text rides as the live message.
        var replay = state.AgentMessages.ToList();
        state.AddAgentMessage("user", trimmed);
        state.SetAgentBusy(true);
        state.PendingAgentJobId = null;
        await PersistAsync("user", trimmed);
        try
        {
            // Task.Run: the framework's tool loop blocks on sync-over-async tool
            // bodies — off the Blazor sync context that's safe, on it it would
            // deadlock the circuit.
            var reply = await Task.Run(() => RunTurnAsync(trimmed, replay));
            state.AddAgentMessage("agent", reply);
            await PersistAsync("agent", reply, state.PendingAgentJobId);
        }
        catch (Exception exc)
        {
            // exc.Message can carry env-var names and endpoints — log it, never
            // render it.
            Console.WriteLine($"Studio agent failed: {exc}");
            state.AddAgentMessage("agent",
                "The editing agent isn't available right now — everything in the "
                + "inspector still works manually.");
        }
        finally
        {
            state.PendingAgentJobId = null;
            state.SetAgentBusy(false);
        }
    }

    private async Task<string> RunTurnAsync(string text, IReadOnlyList<AgentChatMessage> replay)
    {
        var providers = Providers.EditorProviders(title: "highlighter studio");
        if (providers.Count == 0)
            throw new InvalidOperationException("no editor model providers configured");
        while (true)
        {
            try
            {
                var freshSession = await EnsureSessionAsync();
                // A brand-new session (first turn, reload, provider swap) gets
                // the visible transcript replayed so the model keeps its memory.
                var response = freshSession && replay.Count > 0
                    ? await _agent!.RunAsync(
                        ReplayMessages(replay)
                            .Append(new ChatMessage(ChatRole.User, text))
                            .ToList(),
                        _session)
                    : await _agent!.RunAsync(text, _session);
                return string.IsNullOrWhiteSpace(response.Text)
                    ? "Done — anything else on this cut?"
                    : response.Text.Trim();
            }
            catch (Exception exc) when (_providerIndex + 1 < providers.Count)
            {
                Console.WriteLine($"Studio agent provider failed; trying the next: {exc.Message}");
                _providerIndex += 1;
                _agent = null;
                _session = null;
            }
        }
    }

    private static IEnumerable<ChatMessage> ReplayMessages(IReadOnlyList<AgentChatMessage> history) =>
        history
            .Skip(Math.Max(0, history.Count - MaxReplayMessages))
            .Select(message => new ChatMessage(
                message.Role == "user" ? ChatRole.User : ChatRole.Assistant, message.Text));

    /// <summary>Enter (or return to) the conversation for the current context.
    /// Long-form chats are durable: history loads from the API, the greeting is
    /// persisted with it, and a still-unresolved chat job gets its watcher
    /// re-armed. Clip chats stay in-memory.</summary>
    public async Task SeedContextAsync()
    {
        var sameContext = _sessionContext == ContextKey();
        if (!sameContext)
        {
            state.ReplaceAgentMessages([]);
            ResetAgent();
        }
        if (state.AgentContext == "long" && state.ProjectId is { } id)
        {
            await LoadHistoryAsync(id);
            return;
        }
        if (!sameContext)
            state.AddAgentMessage("agent", ClipGreeting());
    }

    private async Task LoadHistoryAsync(Guid id)
    {
        try
        {
            var rows = await api.GetAgentMessagesAsync(id);
            if (rows.Count == 0)
            {
                var greeting = LongGreeting();
                state.ReplaceAgentMessages([new AgentChatMessage("agent", greeting)]);
                await PersistAsync("agent", greeting);
                return;
            }
            state.ReplaceAgentMessages(rows.Select(row =>
                new AgentChatMessage(row.Role, row.Text, row.JobId, row.JobFinal)));
            // A chat-started job with no completion row yet: watch it from this
            // circuit so the outcome lands in the open chat.
            var pending = rows.LastOrDefault(row => row.JobId is { Length: > 0 });
            if (pending?.JobId is { } jobId
                && !rows.Any(row => row.JobId == jobId && row.JobFinal))
                backend.WatchChatJob(jobId);
        }
        catch (Exception exc)
        {
            Console.WriteLine($"Agent chat history load failed: {exc.Message}");
            if (state.AgentMessages.Count == 0)
                state.AddAgentMessage("agent", LongGreeting());
        }
    }

    private string LongGreeting() =>
        $"Working on the long-form cut \"{state.LongformTitle}\". "
        + "I can revise the cut, rerun research, and generate or select thumbnails.";

    private string ClipGreeting() =>
        $"Working on \"{state.ActiveClipTitle}\". I can rerun research, re-render this "
        + "clip in another format, and answer questions about the project.";

    private async Task PersistAsync(string role, string text, string? jobId = null)
    {
        if (state.AgentContext != "long" || state.ProjectId is not { } id) return;
        try
        {
            await api.PostAgentMessageAsync(id, role, text, jobId);
        }
        catch (Exception exc)
        {
            // The in-memory chat still works; durability degrades gracefully.
            Console.WriteLine($"Agent chat persist failed: {exc.Message}");
        }
    }

    /// <summary>Keyed on the project and context — but NOT the long-form
    /// version: a finished revision bumps the version and must keep the
    /// conversation, not wipe it.</summary>
    private string ContextKey() =>
        $"{state.ProjectId}:{state.AgentContext}:{state.ActiveClip?.Id}";

    private void ResetAgent()
    {
        _agent = null;
        _session = null;
        _sessionContext = ContextKey();
    }

    /// <summary>True when a new model session was created (the caller then
    /// replays history into it).</summary>
    private async Task<bool> EnsureSessionAsync()
    {
        if (_agent is not null && _session is not null && _sessionContext == ContextKey()) return false;
        _sessionContext = ContextKey();
        var providers = Providers.EditorProviders(title: "highlighter studio");
        if (providers.Count == 0)
            throw new InvalidOperationException("no editor model providers configured");
        var provider = providers[Math.Clamp(_providerIndex, 0, providers.Count - 1)];
        var client = new PipelineChatClient(provider, timeoutSeconds: 120.0);
        _agent = new ChatClientAgent(client, Instructions(), tools: Tools());
        _session = await _agent.CreateSessionAsync();
        return true;
    }

    private string Instructions()
    {
        var shared =
            "You are the editing agent inside the Highlighter studio, working beside a human "
            + "editor on one project. Use your tools for anything they ask about the project; "
            + "answer in one or two plain sentences and report what actually happened. You "
            + "cannot create new projects — point them at the New project button (top left of "
            + "the Projects page). You cannot publish — that is the Publish button in the "
            + "editor. When something is unavailable, say so plainly and offer the closest "
            + "alternative.";
        return state.AgentContext == "long"
            ? shared + $" Context: the long-form cut \"{state.LongformTitle}\". You "
              + "can revise the cut with revise_longform (a re-cut keeps the thumbnail and "
              + "title), refresh research, and generate, select, or list thumbnails."
            : shared + $" Context: the highlight clip \"{state.ActiveClipTitle}\". Revisions "
              + "apply only to the long-form cut, not clips — if asked to revise, explain "
              + "that and point at the long-form editor or this panel's manual format and "
              + "caption options. You can refresh research and re-render this clip in another "
              + "format with reformat_clip.";
    }

    private List<AITool> Tools()
    {
        var tools = new List<AITool>
        {
            Tool("get_project_status", "The project's status, outputs, and long-form title.",
                NoArgs(), _ => backend.GetProjectStatusAsync().GetAwaiter().GetResult()),
            Tool("list_clips", "Every highlight clip with duration, score, and caption state.",
                NoArgs(), _ => backend.ListClipsAsync().GetAwaiter().GetResult()),
            Tool("get_longform_versions", "The long-form versions and their thumbnails.",
                NoArgs(), _ => backend.GetLongformVersionsAsync().GetAwaiter().GetResult()),
            Tool("rerun_research", "Refresh the web-grounded content research with a focus.",
                Args(("focus", "string", "What the research should dig into.")),
                args => backend.RerunResearchAsync(Str(args, "focus")).GetAwaiter().GetResult()),
            Tool("get_job_status", "Check a started job by its id.",
                Args(("job_id", "string", "The job id a tool returned.")),
                args => backend.GetJobStatusAsync(Str(args, "job_id")).GetAwaiter().GetResult()),
        };
        if (state.AgentContext == "long")
        {
            tools.Add(Tool("revise_longform",
                "Revise the long-form cut from a natural-language request; renders the next version.",
                Args(("request", "string", "What to change in the cut.")),
                args => backend.ReviseLongformAsync(Str(args, "request")).GetAwaiter().GetResult()));
            tools.Add(Tool("generate_thumbnails",
                "Generate another thumbnail concept, optionally steered by a prompt.",
                Args(("prompt", "string", "Optional creative direction.")),
                args => backend.GenerateThumbnailsAsync(Str(args, "prompt")).GetAwaiter().GetResult()));
            tools.Add(Tool("select_thumbnail", "Make a generated variant the video's thumbnail.",
                Args(("index", "number", "The variant number to select.")),
                args => backend.SelectThumbnailAsync(Int(args, "index")).GetAwaiter().GetResult()));
        }
        else
        {
            tools.Add(Tool("reformat_clip",
                "Re-render this clip in another delivery format.",
                Args(
                    ("format", "string", "square for a 1:1 center crop."),
                    ("captions", "boolean", "Whether to burn captions into the new render.")),
                args => backend.ReformatClipAsync(
                    Str(args, "format"),
                    args["captions"]?.GetValue<bool>() ?? false).GetAwaiter().GetResult()));
        }
        return tools;
    }

    private static AITool Tool(
        string name, string description, JsonObject schema, Func<JsonObject, object?> invoke) =>
        new RawSchemaFunction(name, description, schema, invoke);

    private static JsonObject NoArgs() =>
        new() { ["type"] = "object", ["properties"] = new JsonObject() };

    private static JsonObject Args(params (string Name, string Type, string Description)[] args)
    {
        var properties = new JsonObject();
        foreach (var (name, type, description) in args)
        {
            properties[name] = new JsonObject
            {
                ["type"] = type,
                ["description"] = description,
            };
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(args.Select(a => (JsonNode?)a.Name).ToArray()),
        };
    }

    private static string Str(JsonObject args, string key) =>
        args[key]?.GetValue<string>() ?? "";

    /// <summary>Models sometimes emit numbers as strings ("2"); read either.</summary>
    private static int Int(JsonObject args, string key)
    {
        var node = args[key];
        if (node is null) return 0;
        try
        {
            return (int)node.GetValue<double>();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return int.TryParse(node.ToString(), out var parsed) ? parsed : 0;
        }
    }
}
