using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Xunit;

namespace Highlighter.Pipeline.Tests;

/// <summary>Port of pipeline/tests/test_providers.py. Env vars are
/// process-global, so every test runs against a cleared set (saved and
/// restored around each test) and the class opts out of parallelism.</summary>
[Collection("environment")]
public sealed class ProvidersTests : IDisposable
{
    private static readonly string[] AllEnvKeys =
    {
        "OPENROUTER_API_KEY",
        "AZURE_EDITOR_ENDPOINT",
        "AZURE_EDITOR_KEY",
        "AZURE_EDITOR_API_KEY",
        "AZURE_EDITOR_DEPLOYMENT",
        "AZURE_AUDIO_ENDPOINT",
        "AZURE_AUDIO_KEY",
        "AZURE_AUDIO_API_KEY",
        "AZURE_AUDIO_DEPLOYMENT",
        "AZURE_OPENAI_ENDPOINT",
        "AZURE_OPENAI_API_KEY",
        "AZURE_OPENAI_EDIT_DEPLOYMENT",
        "AZURE_OPENAI_AUDIO_DEPLOYMENT",
        "AZURE_REASONING_EFFORT",
    };

    private readonly Dictionary<string, string?> _saved = new();

    public ProvidersTests()
    {
        foreach (var key in AllEnvKeys)
        {
            _saved[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    public void Dispose()
    {
        foreach (var (key, value) in _saved)
            Environment.SetEnvironmentVariable(key, value);
    }

    private static void SetAzure(string prefix, string deployment)
    {
        Environment.SetEnvironmentVariable($"{prefix}_ENDPOINT", "https://example.openai.azure.com");
        Environment.SetEnvironmentVariable($"{prefix}_KEY", "azure-key");
        Environment.SetEnvironmentVariable($"{prefix}_DEPLOYMENT", deployment);
    }

    [Fact]
    public void OpenRouterOnlyWithoutAzureEnv()
    {
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "or-key");
        var providers = Providers.EditorProviders(title: "t", openrouterReasoningEffort: "high");
        Assert.Equal(new[] { "openrouter" }, providers.Select(p => p.Name));
        Assert.False(JsonUtil.Truthy(providers[0].ExtraBody["provider"]?["allow_fallbacks"]));
        Assert.Equal("high", JsonUtil.Str(providers[0].ExtraBody["reasoning"]?["effort"]));
        Assert.Equal(0.0, providers[0].Temperature);
        Assert.True(providers[0].SupportsJsonSchema);
    }

    [Fact]
    public void EditorChainRunsOpenRouterFirstWithAzureAtMaxReasoning()
    {
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "or-key");
        SetAzure("AZURE_EDITOR", "gpt-5.4");
        var providers = Providers.EditorProviders(title: "t");
        Assert.Equal(new[] { "openrouter", "azure" }, providers.Select(p => p.Name));
        var azure = providers[1];
        Assert.Equal("gpt-5.4", azure.Model);
        Assert.Equal("https://example.openai.azure.com/openai/v1", azure.BaseUrl);
        Assert.Equal("xhigh", JsonUtil.Str(azure.ExtraBody["reasoning_effort"]));
        Assert.Null(azure.Temperature); // reasoning deployments reject explicit values
        Assert.True(azure.SupportsJsonSchema);
    }

    [Fact]
    public void DeploymentsBelowGpt54TopOutAtHigh()
    {
        SetAzure("AZURE_EDITOR", "gpt-5-mini");
        var azure = Providers.EditorProviders(title: "t")[0];
        Assert.Equal("high", JsonUtil.Str(azure.ExtraBody["reasoning_effort"]));
    }

    [Fact]
    public void AudioChainRunsGeminiFirst()
    {
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "or-key");
        SetAzure("AZURE_AUDIO", "gpt-audio-mini");
        var providers = Providers.AudioProviders(title: "t");
        Assert.Equal(new[] { "openrouter", "azure" }, providers.Select(p => p.Name));
    }

    [Fact]
    public void AzureAudioHasNoReasoningOrJsonSchema()
    {
        SetAzure("AZURE_AUDIO", "gpt-audio-mini");
        var azure = Providers.AudioProviders(title: "t")[0];
        Assert.Equal(new[] { "modalities" }, ((JsonObject)azure.ExtraBody).Select(p => p.Key));
        Assert.Equal("text", JsonUtil.Str((azure.ExtraBody["modalities"] as JsonArray)?[0]));
        Assert.Equal(0.0, azure.Temperature);
        Assert.False(azure.SupportsJsonSchema);
    }

    [Fact]
    public void SharedAzureOpenAINamesCoverBothRoles()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://shared.openai.azure.com");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "shared-key");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_EDIT_DEPLOYMENT", "gpt-5.4");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_AUDIO_DEPLOYMENT", "gpt-audio-mini");
        Assert.Equal("gpt-5.4", Providers.EditorProviders(title: "t")[0].Model);
        Assert.Equal("gpt-audio-mini", Providers.AudioProviders(title: "t")[0].Model);
    }

    [Fact]
    public void RoleSpecificNamesOverrideSharedOnes()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://shared.openai.azure.com");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "shared-key");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_AUDIO_DEPLOYMENT", "gpt-audio-mini");
        Environment.SetEnvironmentVariable("AZURE_AUDIO_ENDPOINT", "https://audio.openai.azure.com");
        Environment.SetEnvironmentVariable("AZURE_AUDIO_API_KEY", "audio-key");
        var audio = Providers.AudioProviders(title: "t")[0];
        Assert.Equal("https://audio.openai.azure.com/openai/v1", audio.BaseUrl);
        Assert.Equal("audio-key", audio.ApiKey);
        Assert.Equal("gpt-audio-mini", audio.Model);
    }

    [Fact]
    public void EndpointAlreadyEndingInV1IsNotDoubled()
    {
        Environment.SetEnvironmentVariable(
            "AZURE_EDITOR_ENDPOINT", "https://example.openai.azure.com/openai/v1/");
        Environment.SetEnvironmentVariable("AZURE_EDITOR_KEY", "k");
        Environment.SetEnvironmentVariable("AZURE_EDITOR_DEPLOYMENT", "gpt-5.4");
        Assert.Equal(
            "https://example.openai.azure.com/openai/v1",
            Providers.EditorProviders(title: "t")[0].BaseUrl);
    }

    [Fact]
    public void NoConfigurationThrows()
    {
        var error = Assert.Throws<PipelineError>(() => Providers.EditorProviders(title: "t"));
        Assert.Contains("No editor model is configured", error.Message);
    }

    [Fact]
    public void ApplyRequestOptionsShape()
    {
        SetAzure("AZURE_EDITOR", "gpt-5.4");
        var body = new JsonObject();
        Providers.EditorProviders(title: "t")[0].ApplyRequestOptions(body);
        Assert.Equal("gpt-5.4", JsonUtil.Str(body["model"]));
        Assert.False(body.ContainsKey("temperature"));
        Assert.Equal("xhigh", JsonUtil.Str(body["reasoning_effort"]));
    }

    [Fact]
    public void RunWithFallbackUsesSecondProvider()
    {
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "or-key");
        SetAzure("AZURE_AUDIO", "gpt-audio-mini");
        var providers = Providers.AudioProviders(title: "t");
        var (result, provider) = Providers.RunWithFallback(providers, candidate =>
        {
            if (candidate.Name == "openrouter") throw new PipelineError("openrouter down");
            return "ok";
        });
        Assert.Equal("ok", result);
        Assert.Equal("azure", provider.Name);
    }

    [Fact]
    public void RunWithFallbackThrowsLastError()
    {
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "or-key");
        var providers = Providers.AudioProviders(title: "t");
        var error = Assert.Throws<PipelineError>(() =>
            Providers.RunWithFallback<string>(providers, _ => throw new PipelineError("everything down")));
        Assert.Contains("everything down", error.Message);
    }

    [Fact]
    public void ChainLabelNamesTheFallback()
    {
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "or-key");
        SetAzure("AZURE_AUDIO", "gpt-audio-mini");
        var label = Providers.ChainLabel(Providers.AudioProviders(title: "t"));
        Assert.StartsWith("OpenRouter Gemini", label);
        Assert.EndsWith("(fallback: Azure OpenAI (gpt-audio-mini))", label);
    }

    [Fact]
    public void FastTranscriptionParsingMapsToDeepgramWordShape()
    {
        var data = JsonUtil.ParseObject(
            """
            {
              "durationMilliseconds": 4000,
              "combinedPhrases": [{"channel": 0, "text": "Good afternoon. Welcome back."}],
              "phrases": [
                {
                  "offsetMilliseconds": 960,
                  "durationMilliseconds": 640,
                  "text": "Good afternoon.",
                  "confidence": 0.93,
                  "words": [
                    {"text": "Good", "offsetMilliseconds": 960, "durationMilliseconds": 240},
                    {"text": "afternoon.", "offsetMilliseconds": 1200, "durationMilliseconds": 400}
                  ]
                }
              ]
            }
            """);
        var result = Transcribe.ParseFastTranscription(data, locale: "en-US");
        Assert.Equal("Good afternoon. Welcome back.", JsonUtil.Str(result["transcript"]));
        Assert.Equal("azure-speech", JsonUtil.Str(result["backend"]));
        var words = (JsonArray)result["words"]!;
        Assert.Equal("Good", JsonUtil.Str(words[0]?["word"]));
        Assert.Equal("Good", JsonUtil.Str(words[0]?["punctuated_word"]));
        Assert.Equal(0.96, JsonUtil.Double(words[0]?["start"]));
        Assert.Equal(1.2, JsonUtil.Double(words[0]?["end"]));
        Assert.Equal(0.93, JsonUtil.Double(words[0]?["confidence"]));
        Assert.Equal(1.6, JsonUtil.Double(words[1]?["end"]));
    }
}
