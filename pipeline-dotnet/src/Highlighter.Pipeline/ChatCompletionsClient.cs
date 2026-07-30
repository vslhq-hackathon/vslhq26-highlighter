using System.Text;
using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Raised for non-success HTTP statuses — the port of the openai SDK's
/// APIStatusError, carrying the response body for callers that quote it.</summary>
public class ChatCompletionsStatusException : Exception
{
    public int StatusCode { get; }
    public string ResponseText { get; }

    public ChatCompletionsStatusException(int statusCode, string responseText)
        : base($"Chat completions request failed with status {statusCode}")
    {
        StatusCode = statusCode;
        ResponseText = responseText;
    }
}

/// <summary>Chat-completions client over raw HTTP for any OpenAI-compatible
/// endpoint — OpenRouter, or an Azure OpenAI resource's /openai/v1 surface.
///
/// The Python pipeline calls these endpoints through the openai SDK; this port
/// speaks the same wire protocol directly so provider-specific request fields
/// (OpenRouter provider pinning and reasoning, Azure reasoning_effort and
/// modalities) are plain JSON rather than SDK extension points. It also
/// reproduces the SDK behavior the Python code silently relies on: two retries
/// with exponential backoff on 408/429/5xx, connection errors, and timeouts.</summary>
public sealed class ChatCompletionsClient : IDisposable
{
    private const int MaxRetries = 2;
    // Rate limits deserve more patience than transient faults: with the LLM
    // pool running many calls in flight, a 429 burst just needs to queue.
    private const int MaxRetries429 = 4;

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public ChatCompletionsClient(
        string baseUrl,
        string apiKey,
        double timeoutSeconds,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
    }

    /// <summary>POST /chat/completions; returns the parsed response object.
    /// Throws ChatCompletionsStatusException for a non-retryable (or
    /// retry-exhausted) HTTP error status.</summary>
    public JsonObject ChatCompletions(JsonObject body)
    {
        var payload = JsonUtil.Dumps(body);
        for (var attempt = 0; ; attempt++)
        {
            double? retryAfterSeconds = null;
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, _baseUrl + "/chat/completions")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                using var response = _http.Send(request, HttpCompletionOption.ResponseContentRead);
                var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (response.IsSuccessStatusCode) return JsonUtil.ParseObject(text);

                var status = (int)response.StatusCode;
                retryAfterSeconds = RetryAfterSeconds(response);
                var maxAttempts = status == 429 ? MaxRetries429 : MaxRetries;
                var retryable = status == 408 || status == 429 || status >= 500;
                if (!retryable || attempt >= maxAttempts)
                    throw new ChatCompletionsStatusException(status, text);
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                // connection error — retry
            }
            catch (TaskCanceledException) when (attempt < MaxRetries)
            {
                // request timeout — retry (the SDK retries APITimeoutError too)
            }

            Backoff(attempt, retryAfterSeconds);
        }
    }

    /// <summary>Retry-After from a throttled response — delta-seconds or
    /// HTTP-date form — or null when absent/unparsable.</summary>
    internal static double? RetryAfterSeconds(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return Math.Max(0, delta.TotalSeconds);
        if (header?.Date is { } date)
            return Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds);
        return null;
    }

    private static void Backoff(int attempt, double? retryAfterSeconds = null)
    {
        // The openai SDK's schedule: 0.5s * 2^n with jitter, capped at 8s —
        // but never shorter than what the server's Retry-After asked for
        // (itself capped so a hostile header can't stall a worker thread).
        var seconds = Math.Min(0.5 * Math.Pow(2, attempt), 8.0);
        seconds *= 1.0 - Random.Shared.NextDouble() * 0.25;
        if (retryAfterSeconds is { } wanted)
            seconds = Math.Max(seconds, Math.Min(wanted, 60.0));
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }

    public void Dispose() => _http.Dispose();
}
