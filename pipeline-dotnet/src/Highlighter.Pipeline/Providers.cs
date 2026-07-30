using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>One resolved model target in a role's chain — the port of
/// providers.Provider. ApplyRequestOptions is request_kwargs: it stamps the
/// model, temperature (omitted when null — Azure reasoning deployments reject
/// explicit values), and provider-specific extra request fields onto a body.</summary>
public sealed record ChatProvider(
    string Name,
    string Label,
    string BaseUrl,
    string ApiKey,
    string Model,
    double? Temperature,
    bool SupportsJsonSchema,
    JsonObject ExtraBody,
    IReadOnlyDictionary<string, string>? DefaultHeaders)
{
    public ChatCompletionsClient Client(double timeoutSeconds) =>
        new(BaseUrl, ApiKey, timeoutSeconds, DefaultHeaders);

    public void ApplyRequestOptions(JsonObject body)
    {
        body["model"] = Model;
        if (Temperature is double temperature) body["temperature"] = temperature;
        foreach (var (key, value) in ExtraBody) body[key] = value?.DeepClone();
    }
}

/// <summary>Port of highlighter_pipeline/providers.py: the model provider seam —
/// ordered per-role chains over Azure OpenAI and OpenRouter.
///
/// Every LLM call in the pipeline is one of two roles: editor (text and vision
/// calls — long-form pass 2, the auto-reframe framing call) and audio (calls
/// that hear the source — pass-1 clip scoring, the revision agent). Both roles
/// run OpenRouter Gemini first whenever OPENROUTER_API_KEY is set, with the
/// role's Azure deployment as the fallback (role-specific AZURE_EDITOR_* /
/// AZURE_AUDIO_* names, or the shared AZURE_OPENAI_* names).
/// RunWithFallback tries a chain in order, so a
/// missing or failing provider degrades to the next one instead of failing the
/// run. Azure requests go through the resource's OpenAI-compatible v1 endpoint
/// with the deployment name as the model; reasoning deployments run at the
/// highest effort their family accepts (AZURE_REASONING_EFFORT to override),
/// and the gpt-audio family has no structured outputs — callers check
/// SupportsJsonSchema and fall back to a schema embedded in the prompt.</summary>
public static class Providers
{
    public const string OPENROUTER_BASE_URL = "https://openrouter.ai/api/v1";
    public const string OPENROUTER_VERTEX_PROVIDER = "google-vertex";

    /// <summary>The editor-role Azure deployment alone, when configured.</summary>
    public static ChatProvider? AzureEditorProvider()
    {
        return AzureProvider(
            endpointKeys: new[] { "AZURE_EDITOR_ENDPOINT", "AZURE_OPENAI_ENDPOINT" },
            apiKeys: new[] { "AZURE_EDITOR_KEY", "AZURE_EDITOR_API_KEY", "AZURE_OPENAI_API_KEY" },
            deploymentKeys: new[] { "AZURE_EDITOR_DEPLOYMENT", "AZURE_OPENAI_EDIT_DEPLOYMENT" },
            reasoning: true);
    }

    /// <summary>Provider chain for text/vision editorial calls.</summary>
    public static List<ChatProvider> EditorProviders(
        string title, string openrouterReasoningEffort = Defaults.DEFAULT_LLM_REASONING_EFFORT)
    {
        return Chain(
            OpenRouterProvider(title, openrouterReasoningEffort),
            AzureEditorProvider(),
            role: "editor");
    }

    /// <summary>Provider chain for calls that attach source audio.</summary>
    public static List<ChatProvider> AudioProviders(
        string title, string openrouterReasoningEffort = Defaults.DEFAULT_LLM_REASONING_EFFORT)
    {
        return Chain(
            OpenRouterProvider(title, openrouterReasoningEffort),
            AzureProvider(
                endpointKeys: new[] { "AZURE_AUDIO_ENDPOINT", "AZURE_OPENAI_ENDPOINT" },
                apiKeys: new[] { "AZURE_AUDIO_KEY", "AZURE_AUDIO_API_KEY", "AZURE_OPENAI_API_KEY" },
                deploymentKeys: new[] { "AZURE_AUDIO_DEPLOYMENT", "AZURE_OPENAI_AUDIO_DEPLOYMENT" },
                reasoning: false),
            role: "audio");
    }

    public static string ChainLabel(IReadOnlyList<ChatProvider> providers)
    {
        if (providers.Count == 1) return providers[0].Label;
        var fallbacks = string.Join(", ", providers.Skip(1).Select(provider => provider.Label));
        return $"{providers[0].Label} (fallback: {fallbacks})";
    }

    /// <summary>Try the chain in order; returns (result, provider that produced it).</summary>
    public static (T Result, ChatProvider Provider) RunWithFallback<T>(
        IReadOnlyList<ChatProvider> providers, Func<ChatProvider, T> call)
    {
        Exception lastError = new PipelineError("No model provider is configured");
        for (var index = 0; index < providers.Count; index++)
        {
            try
            {
                return (call(providers[index]), providers[index]);
            }
            catch (Exception exc)
            {
                lastError = exc;
                if (index + 1 < providers.Count)
                {
                    Console.WriteLine(
                        $"{providers[index].Label} call failed; falling back to "
                        + $"{providers[index + 1].Label}: {exc.Message}");
                }
            }
        }
        throw lastError;
    }

    /// <summary>The highest reasoning effort the deployment accepts: xhigh exists
    /// only on the gpt-5.4+ line; everything else tops out at high.</summary>
    private static string MaxReasoningEffort(string deployment) =>
        new[] { "5.4", "5.5", "5.6" }.Any(deployment.Contains) ? "xhigh" : "high";

    private static string? FirstEnv(IReadOnlyList<string> keys) =>
        keys.Select(Config.Env).FirstOrDefault(value => !string.IsNullOrEmpty(value));

    private static List<ChatProvider> Chain(
        ChatProvider? first, ChatProvider? second, string role)
    {
        var providers = new[] { first, second }
            .Where(provider => provider is not null)
            .Select(provider => provider!)
            .ToList();
        if (providers.Count == 0)
        {
            throw new PipelineError(
                $"No {role} model is configured: set AZURE_{role.ToUpperInvariant()}_ENDPOINT/KEY/"
                + "DEPLOYMENT (or the shared AZURE_OPENAI_* names) or OPENROUTER_API_KEY");
        }
        return providers;
    }

    private static ChatProvider? AzureProvider(
        IReadOnlyList<string> endpointKeys,
        IReadOnlyList<string> apiKeys,
        IReadOnlyList<string> deploymentKeys,
        bool reasoning)
    {
        var endpoint = FirstEnv(endpointKeys);
        var key = FirstEnv(apiKeys);
        var deployment = FirstEnv(deploymentKeys);
        if (endpoint is null || key is null || deployment is null) return null;

        JsonObject extraBody;
        double? temperature;
        bool supportsJsonSchema;
        if (reasoning)
        {
            // gpt-5.x reasoning deployments: highest effort the family accepts,
            // default temperature (explicit values are rejected), structured
            // outputs supported.
            var effort = Config.Env("AZURE_REASONING_EFFORT");
            extraBody = new JsonObject
            {
                ["reasoning_effort"] = string.IsNullOrEmpty(effort)
                    ? MaxReasoningEffort(deployment)
                    : effort,
            };
            temperature = null;
            supportsJsonSchema = true;
        }
        else
        {
            // gpt-audio deployments: no reasoning knob, no structured outputs;
            // text-only output requested explicitly.
            extraBody = new JsonObject { ["modalities"] = new JsonArray("text") };
            temperature = 0.0;
            supportsJsonSchema = false;
        }

        var baseUrl = endpoint.TrimEnd('/');
        if (!baseUrl.EndsWith("/openai/v1")) baseUrl += "/openai/v1";
        return new ChatProvider(
            Name: "azure",
            Label: $"Azure OpenAI ({deployment})",
            BaseUrl: baseUrl,
            ApiKey: key,
            Model: deployment,
            Temperature: temperature,
            SupportsJsonSchema: supportsJsonSchema,
            ExtraBody: extraBody,
            DefaultHeaders: null);
    }

    private static ChatProvider? OpenRouterProvider(string title, string reasoningEffort)
    {
        var apiKey = Config.Env("OPENROUTER_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return null;
        return new ChatProvider(
            Name: "openrouter",
            Label: $"OpenRouter Gemini ({Defaults.DEFAULT_OPENROUTER_MODEL})",
            BaseUrl: OPENROUTER_BASE_URL,
            ApiKey: apiKey,
            Model: Defaults.DEFAULT_OPENROUTER_MODEL,
            Temperature: 0.0,
            SupportsJsonSchema: true,
            ExtraBody: new JsonObject
            {
                ["provider"] = new JsonObject
                {
                    ["order"] = new JsonArray(OPENROUTER_VERTEX_PROVIDER),
                    ["allow_fallbacks"] = false,
                },
                ["reasoning"] = new JsonObject
                {
                    ["effort"] = string.IsNullOrEmpty(reasoningEffort)
                        ? Defaults.DEFAULT_LLM_REASONING_EFFORT
                        : reasoningEffort,
                },
            },
            DefaultHeaders: new Dictionary<string, string>
            {
                ["HTTP-Referer"] = "http://localhost",
                ["X-OpenRouter-Title"] = title,
            });
    }
}
