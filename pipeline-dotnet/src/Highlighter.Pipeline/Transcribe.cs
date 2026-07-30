using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/transcribe.py.
///
/// Chunk transcription: Azure AI Speech first, Deepgram fallback.
///
/// Azure's fast transcription API runs when AZURE_SPEECH_KEY and
/// AZURE_SPEECH_REGION (or AZURE_SPEECH_ENDPOINT) are set; any Azure failure
/// falls back to Deepgram for that chunk, and Deepgram alone runs when Azure is
/// not configured. Both backends return the same shape — {"transcript",
/// "words", "metadata", "backend"} with Deepgram-style word dicts ({word,
/// punctuated_word, start, end, confidence}, in seconds) — so everything
/// downstream is backend-agnostic.</summary>
public static class Transcribe
{
    public const string AZURE_SPEECH_API_VERSION = "2025-10-15";

    private static readonly HttpClient Http = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    /// <summary>The Speech resource endpoint when configured: AZURE_SPEECH_ENDPOINT
    /// wins, else the regional endpoint built from AZURE_SPEECH_REGION.</summary>
    public static string? AzureSpeechBaseUrl()
    {
        if (string.IsNullOrEmpty(Config.Env("AZURE_SPEECH_KEY"))) return null;
        var endpoint = Config.Env("AZURE_SPEECH_ENDPOINT");
        if (!string.IsNullOrEmpty(endpoint)) return endpoint.TrimEnd('/');
        var region = Config.Env("AZURE_SPEECH_REGION");
        if (!string.IsNullOrEmpty(region))
            return $"https://{region}.api.cognitive.microsoft.com";
        return null;
    }

    public static string TranscriptionBackendLabel() =>
        AzureSpeechBaseUrl() is not null ? "azure-speech (fallback: deepgram)" : "deepgram";

    public static JsonObject TranscribeAudioFile(
        string audioPath, string model = Defaults.DEFAULT_DEEPGRAM_MODEL)
    {
        var baseUrl = AzureSpeechBaseUrl();
        if (baseUrl is not null)
        {
            try
            {
                return AzureFastTranscription(
                    audioPath, key: Config.RequiredEnv("AZURE_SPEECH_KEY"), baseUrl: baseUrl);
            }
            catch (Exception exc)
            {
                Console.WriteLine(
                    $"Azure Speech transcription failed; falling back to Deepgram: {exc.Message}");
            }
        }
        var result = Deepgram.TranscribeAudioFile(audioPath, model);
        result["backend"] = "deepgram";
        return result;
    }

    public static JsonObject AzureFastTranscription(string audioPath, string key, string baseUrl)
    {
        var locale = Config.Env("AZURE_SPEECH_LOCALE");
        if (string.IsNullOrEmpty(locale)) locale = Defaults.DEFAULT_AZURE_SPEECH_LOCALE;
        var definition = JsonUtil.Dumps(new JsonObject
        {
            ["locales"] = new JsonArray(locale),
        });

        var audioBytes = File.ReadAllBytes(audioPath);
        HttpRequestMessage BuildRequest()
        {
            // A fresh message per attempt: HttpClient disposes sent content.
            var content = new MultipartFormDataContent();
            var definitionPart = new StringContent(definition);
            definitionPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Add(definitionPart, "definition");
            var audioPart = new ByteArrayContent(audioBytes);
            audioPart.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audioPart, "audio", Path.GetFileName(audioPath));
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{baseUrl}/speechtotext/transcriptions:transcribe"
                + $"?api-version={AZURE_SPEECH_API_VERSION}")
            {
                Content = content,
            };
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
            return request;
        }

        string payload;
        try
        {
            using var request = BuildRequest();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var response = Http.Send(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if ((int)response.StatusCode == 429)
            {
                // Throttled: one Retry-After-honoring retry keeps the chunk on
                // Azure (word-timing quality) instead of tipping to Deepgram.
                var wait = ChatCompletionsClient.RetryAfterSeconds(response) ?? 2.0;
                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(wait, 30.0)));
                using var retry = BuildRequest();
                using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                using var second = Http.Send(retry, HttpCompletionOption.ResponseContentRead, cts2.Token);
                body = second.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!second.IsSuccessStatusCode)
                    throw new PipelineError($"Azure Speech request failed: {body}");
            }
            else if (!response.IsSuccessStatusCode)
            {
                throw new PipelineError($"Azure Speech request failed: {body}");
            }
            payload = body;
        }
        catch (HttpRequestException exc)
        {
            throw new PipelineError($"Azure Speech request failed: {exc.Message}", exc);
        }
        catch (TaskCanceledException exc)
        {
            throw new PipelineError("Azure Speech request failed: timed out", exc);
        }

        return ParseFastTranscription(JsonUtil.ParseObject(payload), locale);
    }

    /// <summary>Map a fast-transcription response onto the Deepgram word shape the
    /// rest of the pipeline consumes. Azure has no per-word confidence, so each
    /// word carries its phrase's confidence.</summary>
    public static JsonObject ParseFastTranscription(JsonObject data, string locale)
    {
        var words = new JsonArray();
        foreach (var phraseNode in data["phrases"] as JsonArray ?? new JsonArray())
        {
            if (phraseNode is not JsonObject phrase) continue;
            var confidence = phrase.TryGetPropertyValue("confidence", out var c)
                ? c?.DeepClone()
                : null;
            foreach (var wordNode in phrase["words"] as JsonArray ?? new JsonArray())
            {
                if (wordNode is not JsonObject word) continue;
                var offset = JsonUtil.Double(word["offsetMilliseconds"]);
                var duration = JsonUtil.Double(word["durationMilliseconds"]);
                words.Add(new JsonObject
                {
                    ["word"] = JsonUtil.Str(word["text"]),
                    ["punctuated_word"] = JsonUtil.Str(word["text"]),
                    ["start"] = Py.Round(offset / 1000, 3),
                    ["end"] = Py.Round((offset + duration) / 1000, 3),
                    ["confidence"] = confidence?.DeepClone(),
                });
            }
        }
        var transcript = string.Join(" ",
            (data["combinedPhrases"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(phrase => phrase.TryGetPropertyValue("text", out var text)
                ? JsonUtil.Str(text)
                : "")).Trim();
        return new JsonObject
        {
            ["transcript"] = transcript,
            ["words"] = words,
            ["metadata"] = new JsonObject
            {
                ["locale"] = locale,
                ["duration_milliseconds"] = data["durationMilliseconds"]?.DeepClone(),
            },
            ["backend"] = "azure-speech",
        };
    }
}
