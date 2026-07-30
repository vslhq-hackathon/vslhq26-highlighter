using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/thumbnails.py.
///
/// Thumbnail generation for long-form videos.
///
/// One editor-chain call turns the research context, the video title, and the
/// kept segments into three distinct thumbnail concepts (different art
/// directions, not variations of one idea). Each concept is rendered by an
/// Azure-hosted image deployment, seeded with reference frames pulled from the
/// stitched video so the thumbnails show the actual people and scenes. One
/// variant is picked at random as the default; the publish step lets a human
/// browse all of them or import their own.</summary>
public static class Thumbnails
{
    public const int THUMBNAIL_COUNT = 3;
    public const int MAX_REFERENCE_FRAMES = 4;
    public const int REFERENCE_FRAME_WIDTH = 1280;
    // Back-to-back image calls can trip the provider's rate limit; one paced
    // retry per variant rides it out.
    public const int IMAGE_RETRY_SECONDS = 20;

    private static readonly HttpClient Http = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    private const string THUMBNAIL_CONCEPTS_SCHEMA_JSON =
        """
        {
          "type": "object",
          "properties": {
            "concepts": {
              "type": "array",
              "description": "Exactly three thumbnail concepts, each a distinct art direction.",
              "items": {
                "type": "object",
                "properties": {
                  "direction": {
                    "type": "string",
                    "description": "Short name for this concept's art direction."
                  },
                  "image_prompt": {
                    "type": "string",
                    "description": "Complete prompt for the image model: composition, subject, mood, colors, and how to use the reference frames."
                  },
                  "overlay_text": {
                    "type": "string",
                    "description": "Text drawn on the thumbnail, a few words at most. Empty string for none."
                  }
                },
                "required": ["direction", "image_prompt", "overlay_text"],
                "additionalProperties": false
              }
            }
          },
          "required": ["concepts"],
          "additionalProperties": false
        }
        """;

    public static JsonObject ThumbnailConceptsSchema() =>
        JsonUtil.ParseObject(THUMBNAIL_CONCEPTS_SCHEMA_JSON);

    public const string CONCEPTS_SYSTEM_PROMPT =
        """
        You are the thumbnail designer for a video creator. You receive the finished
        video's title, the segments it contains, and research about the creator and
        what performs in their niche.

        Design exactly three thumbnail concepts in three DISTINCT art directions —
        three genuinely different ideas a designer would pitch side by side, not
        small variations of one idea. Lean on the research's thumbnail_patterns for
        what works in this niche, and on the segments for the moments worth showing.
        The image model will receive real frames from the video as references; write
        each image_prompt to build on those frames (the actual people, setting, and
        scene), name which visual elements to keep, and describe composition, mood,
        and color. Thumbnails are 16:9 and must read clearly at small sizes: bold
        subjects, high contrast, minimal clutter. overlay_text is the on-image text —
        keep it to a few punchy words, or empty when the concept works without text.

        Return ONLY one JSON object matching the schema.
        """;

    /// <summary>Generate up to THUMBNAIL_COUNT thumbnail variants for a stitched
    /// long-form video. Returns {"variants": [{index, direction, overlay_text,
    /// local_path}], "selected_index": int} or null when nothing rendered.</summary>
    public static JsonObject? GenerateThumbnails(
        string longformPath,
        IReadOnlyList<JsonObject> entries,
        string? title,
        JsonObject? researchContext,
        string outputDir,
        string reasoningEffort = Defaults.DEFAULT_LLM_REASONING_EFFORT,
        string? guidance = null,
        int startIndex = 1)
    {
        var concepts = ThumbnailConcepts(entries, title, researchContext, reasoningEffort, guidance);
        var referenceImages = ReferenceImages(longformPath, entries);

        Directory.CreateDirectory(outputDir);
        // The variants are independent image-API calls (pure network latency) —
        // generate them together; per-variant retry and non-fatal failure
        // semantics are unchanged, and variants keep their concept order.
        var generated = new JsonObject?[concepts.Count];
        var tasks = new List<Task>();
        for (var offset = 0; offset < concepts.Count; offset++)
        {
            var slot = offset;
            var number = startIndex + slot;
            var concept = concepts[slot];
            var prompt = ImagePrompt(concept, title);
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    (byte[] Bytes, string Extension) image;
                    try
                    {
                        image = GenerateImage(prompt, referenceImages);
                    }
                    catch
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(IMAGE_RETRY_SECONDS));
                        image = GenerateImage(prompt, referenceImages);
                    }
                    var outputPath = Path.Combine(outputDir, $"thumbnail_{number}{image.Extension}");
                    File.WriteAllBytes(outputPath, image.Bytes);
                    generated[slot] = new JsonObject
                    {
                        ["index"] = number,
                        ["direction"] = JsonUtil.C(concept["direction"]),
                        ["overlay_text"] = JsonUtil.C(concept["overlay_text"]),
                        ["local_path"] = outputPath,
                    };
                }
                catch (Exception exc)
                {
                    Console.WriteLine($"Thumbnail {number} generation failed (non-fatal): {exc.Message}");
                }
            }));
        }
        Task.WaitAll(tasks.ToArray());
        var variants = new JsonArray();
        foreach (var variant in generated)
            if (variant is not null) variants.Add(variant);

        if (variants.Count == 0) return null;
        var selected = Random.Shared.Next(variants.Count);
        Console.WriteLine(
            $"Generated {variants.Count} thumbnail variant(s); "
            + $"defaulting to #{JsonUtil.Str(variants[selected]!["index"])} "
            + $"({JsonUtil.Str(variants[selected]!["direction"])})");
        return new JsonObject { ["variants"] = variants, ["selected_index"] = selected };
    }

    private static List<JsonObject> ThumbnailConcepts(
        IReadOnlyList<JsonObject> entries,
        string? title,
        JsonObject? researchContext,
        string reasoningEffort,
        string? guidance = null)
    {
        var prompt = ConceptsPrompt(entries, title, researchContext, guidance);
        var providers = Providers.EditorProviders(
            title: "highlighter thumbnails", openrouterReasoningEffort: reasoningEffort);
        var (decision, _) = Providers.RunWithFallback(
            providers, provider => RequestConcepts(provider, prompt));
        var concepts = (decision["concepts"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Where(concept => JsonUtil.Truthy(concept["image_prompt"]))
            .Take(THUMBNAIL_COUNT)
            .ToList();
        if (concepts.Count == 0)
            throw new PipelineError("thumbnail concept call returned no usable concepts");
        return concepts;
    }

    public static string ConceptsPrompt(
        IReadOnlyList<JsonObject> entries, string? title, JsonObject? researchContext,
        string? guidance = null)
    {
        var lines = new List<string> { "Video brief:" };
        if (!string.IsNullOrEmpty(title)) lines.Add($"- Title: {title}");
        if (!string.IsNullOrEmpty(guidance)) lines.Add($"- Human guidance: {guidance}");
        lines.Add("- Segments, in order:");
        foreach (var entry in entries)
        {
            var summary = JsonUtil.StrOrNull(entry["description"]) ?? "";
            var entryTitle = JsonUtil.Truthy(entry["title"]) ? JsonUtil.Str(entry["title"]) : "Untitled";
            lines.Add($"  - {entryTitle}: {summary}".TrimEnd(':', ' '));
        }
        if (researchContext is not null)
        {
            lines.Add("");
            lines.Add("Research context:");
            lines.Add(JsonUtil.DumpsIndented(researchContext));
        }
        return string.Join("\n", lines);
    }

    private static JsonObject RequestConcepts(ChatProvider provider, string prompt)
    {
        var content = Agents.RunAgentText(
            provider,
            instructions: CONCEPTS_SYSTEM_PROMPT,
            prompt: prompt,
            timeoutSeconds: 180.0,
            extraOptions: new JsonObject
            {
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = "thumbnail_concepts",
                        ["schema"] = ThumbnailConceptsSchema(),
                    },
                },
            });
        return Llm.JsonFromText(content);
    }

    public static string ImagePrompt(JsonObject concept, string? title)
    {
        var parts = new List<string> { JsonUtil.Str(concept["image_prompt"]) };
        var overlay = (JsonUtil.StrOrNull(concept["overlay_text"]) ?? "").Trim();
        parts.Add(overlay.Length > 0
            ? $"Render the text \"{overlay}\" boldly on the image, large and legible."
            : "Do not render any text on the image.");
        if (!string.IsNullOrEmpty(title)) parts.Add($"The video is titled \"{title}\".");
        parts.Add("16:9 video thumbnail; must read clearly at small sizes.");
        return string.Join(" ", parts);
    }

    /// <summary>Sample frames at kept-segment midpoints (in the stitched video's own
    /// timeline) and return them as data URIs for the image model.</summary>
    private static List<string> ReferenceImages(
        string longformPath, IReadOnlyList<JsonObject> entries)
    {
        var images = new List<string>();
        var tmp = Directory.CreateTempSubdirectory("thumbnails-");
        try
        {
            var offsets = ReferenceOffsets(entries);
            for (var index = 0; index < offsets.Count; index++)
            {
                var framePath = Path.Combine(tmp.FullName, $"frame_{index}.jpg");
                try
                {
                    Render.ExtractThumbnail(
                        longformPath, framePath, offsets[index], maxWidth: REFERENCE_FRAME_WIDTH);
                }
                catch (Exception exc)
                {
                    Console.WriteLine(
                        $"Thumbnail reference frame at {Py.F(offsets[index], 1)}s failed: {exc.Message}");
                    continue;
                }
                var frameB64 = Convert.ToBase64String(File.ReadAllBytes(framePath));
                images.Add($"data:image/jpeg;base64,{frameB64}");
            }
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
        return images;
    }

    /// <summary>Midpoints of up to MAX_REFERENCE_FRAMES kept segments, expressed in
    /// the stitched output's timeline (segments are concatenated in order).</summary>
    public static List<double> ReferenceOffsets(IReadOnlyList<JsonObject> entries)
    {
        var midpoints = new List<double>();
        var elapsed = 0.0;
        foreach (var entry in entries)
        {
            var duration = JsonUtil.Double(entry["end_seconds"]) - JsonUtil.Double(entry["start_seconds"]);
            midpoints.Add(elapsed + duration / 2);
            elapsed += duration;
        }
        if (midpoints.Count <= MAX_REFERENCE_FRAMES) return midpoints;
        var step = (midpoints.Count - 1) / (double)(MAX_REFERENCE_FRAMES - 1);
        return Enumerable.Range(0, MAX_REFERENCE_FRAMES)
            .Select(i => midpoints[(int)Math.Round(i * step, MidpointRounding.ToEven)])
            .ToList();
    }

    /// <summary>The model that renders thumbnails on this configuration, for run
    /// metadata.</summary>
    public static string ThumbnailModelLabel()
    {
        if (!string.IsNullOrEmpty(Config.Env("OPENROUTER_API_KEY")))
            return Defaults.DEFAULT_THUMBNAIL_MODEL;
        var azure = AzureImageConfig();
        return azure is not null ? azure.Value.Deployment : Defaults.DEFAULT_THUMBNAIL_MODEL;
    }

    /// <summary>Whether any image model is reachable on this configuration.</summary>
    public static bool ThumbnailsConfigured() =>
        AzureImageConfig() is not null || !string.IsNullOrEmpty(Config.Env("OPENROUTER_API_KEY"));

    /// <summary>Render one thumbnail image. Returns (image bytes, file extension).
    ///
    /// Model resolution, kept here in one place: Nano Banana 2
    /// (google/gemini-3.1-flash-image) pinned to Google Vertex through
    /// OpenRouter's chat API takes the call whenever OPENROUTER_API_KEY is set
    /// (images come back as data URIs). When OpenRouter is unconfigured or the
    /// call fails, the render falls through to the Azure-hosted image
    /// deployment (gpt-image-2) when one is configured — AZURE_IMAGE_DEPLOYMENT
    /// or AZURE_OPENAI_IMAGE_DEPLOYMENT, with AZURE_IMAGE_ENDPOINT/
    /// AZURE_IMAGE_KEY overriding the shared Azure OpenAI endpoint and key —
    /// using images/edits when reference frames exist and images/generations
    /// when none survived extraction. Callers only ever see this one
    /// function.</summary>
    private static (byte[] Bytes, string Extension) GenerateImage(
        string prompt, IReadOnlyList<string> referenceImages)
    {
        var azure = AzureImageConfig();
        if (!string.IsNullOrEmpty(Config.Env("OPENROUTER_API_KEY")))
        {
            try
            {
                return NanoBananaImage(prompt, referenceImages);
            }
            catch (Exception exc) when (azure is not null)
            {
                Console.WriteLine(
                    $"{Defaults.DEFAULT_THUMBNAIL_MODEL} image call failed; falling back to "
                    + $"Azure OpenAI ({azure.Value.Deployment}): {exc.Message}");
            }
        }
        if (azure is null)
        {
            // No OpenRouter key either — surfaces the missing-config error.
            return NanoBananaImage(prompt, referenceImages);
        }
        return AzureImage(
            prompt, referenceImages,
            azure.Value.Endpoint, azure.Value.Key, azure.Value.Deployment);
    }

    private static (string Endpoint, string Key, string Deployment)? AzureImageConfig()
    {
        var endpoint = Config.EnvOrNull("AZURE_IMAGE_ENDPOINT")
            ?? Config.EnvOrNull("AZURE_OPENAI_ENDPOINT");
        var key = Config.EnvOrNull("AZURE_IMAGE_KEY")
            ?? Config.EnvOrNull("AZURE_IMAGE_API_KEY")
            ?? Config.EnvOrNull("AZURE_OPENAI_API_KEY");
        var deployment = Config.EnvOrNull("AZURE_IMAGE_DEPLOYMENT")
            ?? Config.EnvOrNull("AZURE_OPENAI_IMAGE_DEPLOYMENT");
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key)
            || string.IsNullOrEmpty(deployment))
        {
            return null;
        }
        return (endpoint.TrimEnd('/'), key, deployment);
    }

    private static (byte[] Bytes, string Extension) AzureImage(
        string prompt,
        IReadOnlyList<string> referenceImages,
        string endpoint,
        string key,
        string deployment)
    {
        string payload;
        try
        {
            using var request = referenceImages.Count > 0
                ? new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/openai/v1/images/edits")
                {
                    Content = AzureEditsContent(prompt, referenceImages, deployment),
                }
                : new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/openai/v1/images/generations")
                {
                    Content = new StringContent(
                        JsonUtil.Dumps(new JsonObject
                        {
                            ["model"] = deployment,
                            ["prompt"] = prompt,
                            ["size"] = "1536x1024",
                        }),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
            request.Headers.TryAddWithoutValidation("api-key", key);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
            using var response = Http.Send(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new PipelineError($"Image request failed: {responseBody}");
            payload = responseBody;
        }
        catch (HttpRequestException exc)
        {
            throw new PipelineError($"Image request failed: {exc.Message}", exc);
        }
        catch (TaskCanceledException exc)
        {
            throw new PipelineError("Image request failed: timed out", exc);
        }

        var data = JsonUtil.ParseObject(payload)["data"] as JsonArray;
        var encoded = data is { Count: > 0 } ? JsonUtil.StrOrNull(data[0]?["b64_json"]) : null;
        if (string.IsNullOrEmpty(encoded))
            throw new PipelineError("image response contained no image data");
        return (Convert.FromBase64String(encoded), ".png");
    }

    private static MultipartFormDataContent AzureEditsContent(
        string prompt, IReadOnlyList<string> referenceImages, string deployment)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(deployment), "model" },
            { new StringContent(prompt), "prompt" },
            { new StringContent("1536x1024"), "size" },
        };
        for (var index = 0; index < referenceImages.Count; index++)
        {
            var encoded = referenceImages[index];
            var frame = new ByteArrayContent(
                Convert.FromBase64String(encoded[(encoded.IndexOf(',') + 1)..]));
            frame.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Add(frame, "image[]", $"frame_{index}.jpg");
        }
        return content;
    }

    private static (byte[] Bytes, string Extension) NanoBananaImage(
        string prompt, IReadOnlyList<string> referenceImages)
    {
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = prompt },
        };
        foreach (var image in referenceImages)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = image },
            });
        }
        var body = new JsonObject
        {
            ["model"] = Defaults.DEFAULT_THUMBNAIL_MODEL,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = content }),
            ["modalities"] = new JsonArray("image", "text"),
            ["provider"] = new JsonObject
            {
                ["order"] = new JsonArray(Providers.OPENROUTER_VERTEX_PROVIDER),
                ["allow_fallbacks"] = false,
            },
        };

        string payload;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{Providers.OPENROUTER_BASE_URL}/chat/completions")
            {
                Content = new StringContent(
                    JsonUtil.Dumps(body), System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(
                "Authorization", $"Bearer {Config.RequiredEnv("OPENROUTER_API_KEY")}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
            using var response = Http.Send(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new PipelineError($"Image request failed: {responseBody}");
            payload = responseBody;
        }
        catch (HttpRequestException exc)
        {
            throw new PipelineError($"Image request failed: {exc.Message}", exc);
        }
        catch (TaskCanceledException exc)
        {
            throw new PipelineError("Image request failed: timed out", exc);
        }

        var choices = JsonUtil.ParseObject(payload)["choices"] as JsonArray;
        var images = choices is { Count: > 0 }
            ? choices[0]?["message"]?["images"] as JsonArray
            : null;
        var dataUri = images is { Count: > 0 }
            ? JsonUtil.StrOrNull(images[0]?["image_url"]?["url"]) ?? ""
            : "";
        var separator = dataUri.IndexOf(',');
        if (separator < 0)
            throw new PipelineError("image response contained no image data");
        var prefix = dataUri[..separator];
        var extension = prefix.Contains("image/png") ? ".png" : ".jpg";
        return (Convert.FromBase64String(dataUri[(separator + 1)..]), extension);
    }
}
