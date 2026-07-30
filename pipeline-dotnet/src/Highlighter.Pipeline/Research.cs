using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/research.py.
///
/// Content research layer: one research call per run, specialized per mode.
///
/// The call runs first as a single web-grounded Claude Sonnet 5 request
/// through OpenRouter's web-search plugin; the Azure OpenAI editor deployment
/// takes over when OpenRouter is unconfigured or fails. Either way the
/// result is structured, source-cited editorial context that feeds the editing
/// models as researchContext. The prompt and schema are specialized per
/// pipeline mode: short form researches clip formats, hooks, and platform
/// norms; long form researches video structure, pacing, and retention.</summary>
public static class Research
{
    // Fields both modes research: who the creator is, who watches, and what
    // the audience already knows. The mode-specific fields below cover what
    // "performs well" means for that output format.
    private const string CORE_RESEARCH_PROPERTIES_JSON =
        """
        {
          "creator_profile": {"type": "string"},
          "content_context": {"type": "string"},
          "target_audience": {"type": "array", "items": {"type": "string"}},
          "inside_references": {"type": "array", "items": {"type": "string"}},
          "recent_context": {"type": "array", "items": {"type": "string"}},
          "thumbnail_patterns": {"type": "array", "items": {"type": "string"}},
          "avoid": {"type": "array", "items": {"type": "string"}}
        }
        """;

    private const string SHORTFORM_RESEARCH_PROPERTIES_JSON =
        """
        {
          "successful_clip_patterns": {"type": "array", "items": {"type": "string"}},
          "useful_hooks": {"type": "array", "items": {"type": "string"}},
          "platform_notes": {"type": "array", "items": {"type": "string"}}
        }
        """;

    private const string LONGFORM_RESEARCH_PROPERTIES_JSON =
        """
        {
          "structure_patterns": {"type": "array", "items": {"type": "string"}},
          "pacing_and_retention": {"type": "array", "items": {"type": "string"}},
          "title_patterns": {"type": "array", "items": {"type": "string"}}
        }
        """;

    private const string SOURCES_PROPERTY_JSON =
        """
        {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "title": {"type": "string"},
              "url": {"type": "string"},
              "claim": {"type": "string"}
            },
            "required": ["title", "url", "claim"],
            "additionalProperties": false
          }
        }
        """;

    /// <summary>The research response schema for a pipeline mode ('short' or 'long').</summary>
    public static JsonObject ResearchSchema(string pipelineMode)
    {
        var properties = JsonUtil.ParseObject(CORE_RESEARCH_PROPERTIES_JSON);
        var modeProperties = JsonUtil.ParseObject(
            pipelineMode == "short"
                ? SHORTFORM_RESEARCH_PROPERTIES_JSON
                : LONGFORM_RESEARCH_PROPERTIES_JSON);
        foreach (var key in modeProperties.Select(pair => pair.Key).ToList())
        {
            var value = modeProperties[key];
            modeProperties.Remove(key);
            properties[key] = value;
        }
        properties["sources"] = JsonUtil.Parse(SOURCES_PROPERTY_JSON);
        var required = new JsonArray();
        foreach (var pair in properties) required.Add(pair.Key);
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private const string RESEARCH_SYSTEM_PROMPT_TEMPLATE =
        """
        You are a content researcher for an AI video editing
        pipeline. Given a source video (creator/channel, platform, live or VOD), use
        web search to build practical, current editorial context for the editor model
        that will {editing_goal}.

        Research and report:
        - Who the creator is and what their content/channel is about.
        - The target audience: who watches this, what they care about.
        - Inside references: recurring jokes, lore, memes, nicknames, callbacks, and
          running bits this creator's audience recognizes instantly.
        - Recent context: what has happened lately with this creator and in this niche
          (events, drama, releases, discourse) that a moment might reference.
        - The content genre and what is currently trending or discussed in it.
        {mode_points}

        Rules:
        - Keep every field compact and practical: the consumer is another LLM making
          editing decisions, not a human reading a report.
        - Every factual outside-world claim must be backed by an entry in sources with
          a real URL you found via search.
        - If you cannot find much about this specific creator, research the genre and
          platform patterns instead and say so in creator_profile.
        - Return ONLY one JSON object matching the provided schema. No markdown fences,
          no commentary before or after.
        """;

    private const string SHORTFORM_RESEARCH_POINTS =
        """
        - Clip formats and editing patterns that perform well as short-form vertical
          video for similar content.
        - Hooks that work in this niche: cold opens, first-line patterns, what stops
          the scroll.
        - Platform notes: what TikTok/Reels/Shorts/X each reward or punish for this
          kind of content.
        - Thumbnail patterns that work in this niche.
        - What to avoid (overdone formats, sensitive topics for this audience).
        """;

    private const string LONGFORM_RESEARCH_POINTS =
        """
        - Structures that hold viewers in long-form videos for this niche: how
          successful videos open, how they are sequenced or chaptered, how they close.
        - Pacing and retention patterns: where similar videos lose viewers, and what
          editing rhythm keeps them watching.
        - Title and thumbnail patterns that work for long uploads in this niche.
        - What to avoid (overdone formats, sensitive topics for this audience).
        """;

    /// <summary>The research system prompt for a pipeline mode ('short' or 'long').</summary>
    public static string ResearchSystemPrompt(string pipelineMode)
    {
        return pipelineMode == "short"
            ? RESEARCH_SYSTEM_PROMPT_TEMPLATE
                .Replace("{editing_goal}", "select short-form vertical clips from it")
                .Replace("{mode_points}", SHORTFORM_RESEARCH_POINTS.TrimEnd())
            : RESEARCH_SYSTEM_PROMPT_TEMPLATE
                .Replace("{editing_goal}", "cut one long-form edit from it")
                .Replace("{mode_points}", LONGFORM_RESEARCH_POINTS.TrimEnd());
    }

    public static string ResearchBackendLabel()
    {
        var azure = Providers.AzureEditorProvider();
        var primary = $"web-grounded {Defaults.DEFAULT_RESEARCH_MODEL}";
        if (string.IsNullOrEmpty(Config.Env("OPENROUTER_API_KEY")))
            return azure?.Label ?? primary;
        return azure is not null ? $"{primary} (fallback: {azure.Label})" : primary;
    }

    /// <summary>Run the research call and return an object matching
    /// ResearchSchema(pipelineMode). Throws on failure; callers treat research
    /// as best-effort and continue without it.</summary>
    public static JsonObject ResearchContentContext(
        JsonObject sourceContext,
        string pipelineMode = "short",
        string? userInstructions = null,
        string model = Defaults.DEFAULT_RESEARCH_MODEL)
    {
        var prompt = ResearchPrompt(
            sourceContext: sourceContext,
            pipelineMode: pipelineMode,
            userInstructions: userInstructions);
        var systemPrompt = ResearchSystemPrompt(pipelineMode);
        var azure = Providers.AzureEditorProvider();
        if (!string.IsNullOrEmpty(Config.Env("OPENROUTER_API_KEY")))
        {
            try
            {
                return WebGroundedResearch(prompt, model, systemPrompt);
            }
            catch (Exception exc) when (azure is not null)
            {
                Console.WriteLine(
                    $"Web-grounded {model} research failed; falling back to "
                    + $"{azure.Label}: {exc.Message}");
            }
        }
        if (azure is null)
        {
            // Mirrors WebGroundedResearch's config error for the nothing-set case.
            throw new PipelineError("OPENROUTER_API_KEY is required for content research");
        }
        return RequestAzureResearch(azure, prompt, systemPrompt, ResearchSchema(pipelineMode));
    }

    private static string ResearchPrompt(
        JsonObject sourceContext, string pipelineMode, string? userInstructions)
    {
        var goal = pipelineMode == "short"
            ? "The editor is cutting short-form vertical clips (TikTok/Reels/Shorts/X) from this source."
            : "The editor is cutting one long-form edit (a YouTube-ready video) from this source.";
        var promptParts = new List<string>
        {
            "Source video:",
            JsonUtil.DumpsIndented(sourceContext),
            "",
            goal,
        };
        if (!string.IsNullOrEmpty(userInstructions))
        {
            promptParts.AddRange(new[]
            {
                "",
                "The user gave the editor these instructions (use them to focus your research):",
                userInstructions,
            });
        }
        promptParts.AddRange(new[]
        {
            "",
            "Return only a JSON object matching this schema:",
            JsonUtil.DumpsIndented(ResearchSchema(pipelineMode)),
        });
        return string.Join("\n", promptParts);
    }

    private static JsonObject RequestAzureResearch(
        ChatProvider provider, string prompt, string systemPrompt, JsonObject schema)
    {
        var content = Agents.RunAgentText(
            provider,
            instructions: systemPrompt,
            prompt: prompt,
            timeoutSeconds: 300.0,
            extraOptions: new JsonObject
            {
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = "content_research",
                        ["schema"] = schema,
                    },
                },
            });
        var context = JsonFromText(content) as JsonObject
            ?? throw new PipelineError($"{provider.Label} research response was not a JSON object");
        if (!context.ContainsKey("sources")) context["sources"] = new JsonArray();
        return context;
    }

    private static JsonObject WebGroundedResearch(string prompt, string model, string systemPrompt)
    {
        var apiKey = Config.Env("OPENROUTER_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            throw new PipelineError("OPENROUTER_API_KEY is required for content research");

        var provider = new ChatProvider(
            Name: "openrouter",
            Label: $"Web-grounded research ({model})",
            BaseUrl: Providers.OPENROUTER_BASE_URL,
            ApiKey: apiKey,
            Model: model,
            Temperature: 0.3,
            SupportsJsonSchema: true,
            ExtraBody: new JsonObject
            {
                ["plugins"] = new JsonArray
                {
                    new JsonObject { ["id"] = "web", ["max_results"] = 5 },
                },
            },
            DefaultHeaders: new Dictionary<string, string>
            {
                ["HTTP-Referer"] = "http://localhost",
                ["X-OpenRouter-Title"] = "highlighter research",
            });
        var content = Agents.RunAgentText(
            provider,
            instructions: systemPrompt,
            prompt: prompt,
            timeoutSeconds: 300.0,
            extraOptions: new JsonObject { ["max_tokens"] = 8000 });
        var context = JsonFromText(content) as JsonObject
            ?? throw new PipelineError("Research response was not a JSON object");
        if (!context.ContainsKey("sources")) context["sources"] = new JsonArray();
        return context;
    }

    private static JsonNode? JsonFromText(string text)
    {
        var stripped = text.Trim();
        if (stripped.StartsWith("```"))
        {
            stripped = stripped.Trim('`').Trim();
            if (stripped.StartsWith("json")) stripped = stripped[4..].Trim();
        }
        // Web-grounded responses sometimes lead with prose despite instructions;
        // fall back to the outermost JSON object.
        try
        {
            return JsonUtil.Parse(stripped);
        }
        catch (System.Text.Json.JsonException)
        {
            var start = stripped.IndexOf('{');
            var end = stripped.LastIndexOf('}');
            if (start == -1 || end <= start) throw;
            return JsonUtil.Parse(stripped[start..(end + 1)]);
        }
    }
}
