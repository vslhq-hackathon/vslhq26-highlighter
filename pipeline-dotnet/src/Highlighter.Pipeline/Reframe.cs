using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/reframe.py.
///
/// Auto-reframe rendered short-form clips to a 9:16 vertical.
///
/// One framing call PER SAMPLED FRAME decides where the sharp region sits for
/// the span that frame governs (until the next sampled frame): the model sees
/// that one frame plus the transcript words spoken in its span, and returns
/// either a horizontal 1:1 crop center or wide. Frames are sampled at the clip
/// start, every few seconds, and just after each scene cut. Spans whose words
/// and action don't fit one square (split layouts with both sides active, wide
/// action, full-frame graphics) go wide and show the whole 16:9 frame fitted
/// to the canvas width instead. Rendering is deterministic: a full-height
/// square crop at those centers (or the fitted wide frame) fills the width of a
/// 720x1280 canvas, with a blurred, darkened zoom-fill of the same frame above
/// and below. Framing is static between keyframes — hard cuts, no tracking.</summary>
public static class Reframe
{
    public const int CANVAS_WIDTH = 720;
    public const int CANVAS_HEIGHT = 1280;
    public const int BLUR_SIGMA = 25;
    // Periodic frames plus one per shot; enough to catch a subject drifting
    // within a long static shot.
    public const int MAX_SAMPLE_FRAMES = 24;
    // A periodic frame this close to a cut frame shows the same moment twice.
    public const double PERIODIC_FRAME_MIN_GAP_SECONDS = 2.0;
    // Crop moves closer together than this read as jitter, not reframing.
    // The center dead-band absorbs per-frame disagreement: independent framing
    // calls on a static subject answer within a few percent of each other, and
    // a hard cut that shifts the crop under ~8% of the width reads as a glitch.
    // Real reframes (pane or speaker switches) move far more.
    public const double MIN_KEYFRAME_SPACING_SECONDS = 1.5;
    public const double MIN_CENTER_DELTA = 0.08;
    // Framing is a perceptual where's-the-subject call; on the Gemini link of
    // the chain, deep reasoning only adds latency per clip. (The Azure
    // deployment always runs at its own AZURE_REASONING_EFFORT.)
    public const string REFRAME_REASONING_EFFORT = "low";
    // Per-frame framing calls in flight per clip — pure API latency, no CPU.
    public const int FRAME_CALL_CONCURRENCY = 6;
    // Enough transcript for any span; a runaway span can't bloat the prompt.
    private const int MAX_SPAN_WORDS_CHARS = 1200;

    public const string REFRAME_SYSTEM_PROMPT =
        """
        You frame one moment of a 16:9 video for a vertical (9:16) short. The render
        overlays a sharp region on a blurred fill of the same frame. Two choices:
        - Square crop: a full-height 1:1 crop, placed by center_x (fraction of frame
          width: 0 = left edge, 0.5 = middle, 1 = right). Pick the square that holds
          whoever or whatever produces the words and action in this span's
          transcript — the speaker's face or pane, the demo, the play.
        - Wide (wide = true): the whole 16:9 frame fitted into the vertical with
          blurred padding. This is the fallback: use it only when no single square
          captures most of the span — two far-apart people both active, wide action,
          full-frame graphics.

        Judge with the transcript: would a viewer seeing only your square follow every
        word and action in this span? If yes, crop; if no square works, go wide. Many
        frames are small panes on a dead background (stream layouts): crop to the
        active pane — wide only shrinks it further. Ignore chat overlays, tickers, and
        empty margins; center on faces, not on a pane's geometric center.

        Return only JSON matching the schema.
        """;

    private const string FRAME_RESPONSE_SCHEMA_JSON =
        """
        {
          "type": "object",
          "properties": {
            "center_x": {
              "type": "number",
              "description": "Horizontal center of the 1:1 crop as a fraction of frame width. Use 0.5 when wide is true.",
              "minimum": 0,
              "maximum": 1
            },
            "wide": {
              "type": "boolean",
              "description": "True to show the whole 16:9 frame fitted with blurred padding instead of a square crop."
            }
          },
          "required": ["center_x", "wide"],
          "additionalProperties": false
        }
        """;

    public static JsonObject FrameResponseSchema() =>
        JsonUtil.ParseObject(FRAME_RESPONSE_SCHEMA_JSON);

    /// <summary>One framing call per sampled frame, deciding the crop keyframes
    /// for a rendered clip.
    ///
    /// sceneCuts are clip-relative seconds; transcriptWords are clip-relative
    /// {word|punctuated_word, start, end} objects (null → framing runs on the
    /// images alone). Frame calls run concurrently; a failed frame is skipped
    /// so the previous framing extends across its span. Returns
    /// {keyframes, notes, model} with keyframes validated (first at 0, sorted,
    /// de-jittered); throws when every frame call fails — the caller keeps the
    /// 16:9 clip.</summary>
    public static JsonObject PlanCropTrack(
        string clipPath,
        double clipDurationSeconds,
        IReadOnlyList<double> sceneCuts,
        string title,
        string description,
        JsonArray? transcriptWords = null,
        double frameIntervalSeconds = Defaults.DEFAULT_REFRAME_FRAME_INTERVAL_SECONDS)
    {
        var sampleTimes = SampleTimes(clipDurationSeconds, sceneCuts, frameIntervalSeconds);
        var frames = new List<string>(sampleTimes.Count);
        using (var tmp = new TempDir(prefix: "reframe-"))
        {
            for (var index = 0; index < sampleTimes.Count; index++)
            {
                var framePath = Path.Combine(tmp.Path, $"frame_{index}.jpg");
                Render.ExtractThumbnail(
                    clipPath: clipPath, outputPath: framePath, atSeconds: sampleTimes[index]);
                frames.Add(Convert.ToBase64String(File.ReadAllBytes(framePath)));
            }
        }

        var providers = Providers.EditorProviders(
            title: "highlighter reframe",
            openrouterReasoningEffort: REFRAME_REASONING_EFFORT);
        var results = new JsonObject?[sampleTimes.Count];
        var servedBy = new ChatProvider?[sampleTimes.Count];
        using var slots = new SemaphoreSlim(FRAME_CALL_CONCURRENCY);
        var workers = new List<Task>(sampleTimes.Count);
        for (var index = 0; index < sampleTimes.Count; index++)
        {
            var slot = index;
            slots.Wait();
            workers.Add(Task.Run(() =>
            {
                try
                {
                    var spanStart = sampleTimes[slot];
                    var spanEnd = slot + 1 < sampleTimes.Count
                        ? sampleTimes[slot + 1]
                        : clipDurationSeconds;
                    var content = FrameContent(
                        frames[slot], spanStart, spanEnd, clipDurationSeconds,
                        title, description,
                        SpanWordsText(transcriptWords, spanStart, spanEnd));
                    var (framing, provider) = Providers.RunWithFallback(
                        providers, candidate => RequestFraming(candidate, content));
                    results[slot] = framing;
                    servedBy[slot] = provider;
                }
                catch (Exception exc)
                {
                    Console.WriteLine(
                        $"Framing call for frame at {Py.F(sampleTimes[slot], 1)}s failed "
                        + $"(previous framing extends): {exc.Message}");
                }
                finally
                {
                    slots.Release();
                }
            }));
        }
        Task.WaitAll(workers.ToArray());

        var rawKeyframes = new JsonArray();
        for (var index = 0; index < sampleTimes.Count; index++)
        {
            if (results[index] is not { } framing) continue;
            rawKeyframes.Add(new JsonObject
            {
                ["start_seconds"] = sampleTimes[index],
                ["center_x"] = JsonUtil.C(framing["center_x"]),
                ["wide"] = JsonUtil.Truthy(framing["wide"]),
            });
        }
        if (rawKeyframes.Count == 0)
            throw new PipelineError("Every per-frame framing call failed");

        var keyframes = new JsonArray();
        foreach (var keyframe in ValidateKeyframes(rawKeyframes, clipDurationSeconds))
            keyframes.Add(keyframe);
        var wideCount = keyframes.Count(k => JsonUtil.Truthy((k as JsonObject)?["wide"]));
        return new JsonObject
        {
            ["keyframes"] = keyframes,
            ["notes"] = $"per-frame framing: {rawKeyframes.Count}/{sampleTimes.Count} frames "
                + $"answered, {keyframes.Count - wideCount} crop / {wideCount} wide span(s)",
            ["model"] = servedBy.FirstOrDefault(p => p is not null)?.Model ?? "",
        };
    }

    /// <summary>The transcript line for one frame's span: words overlapping
    /// [spanStart, spanEnd), joined in order, capped. Empty when no words.</summary>
    public static string SpanWordsText(JsonArray? words, double spanStart, double spanEnd)
    {
        if (words is null) return "";
        var parts = new List<string>();
        var length = 0;
        foreach (var node in words)
        {
            if (node is not JsonObject word) continue;
            if (!JsonUtil.TryDouble(word["start"], out var start)
                || !JsonUtil.TryDouble(word["end"], out var end))
                continue;
            if (end <= spanStart || start >= spanEnd) continue;
            var text = JsonUtil.StrOrNull(word["punctuated_word"])
                ?? JsonUtil.StrOrNull(word["word"]) ?? "";
            if (text.Length == 0) continue;
            if (length + text.Length + 1 > MAX_SPAN_WORDS_CHARS) break;
            parts.Add(text);
            length += text.Length + 1;
        }
        return string.Join(" ", parts);
    }

    private static JsonArray FrameContent(
        string frameB64,
        double spanStart,
        double spanEnd,
        double clipDurationSeconds,
        string title,
        string description,
        string spanWords)
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = string.Join("\n", new[]
                {
                    $"Clip: {title}",
                    $"What happens: {description}",
                    $"This frame is at {Py.F(spanStart, 1)}s of {Py.F(clipDurationSeconds, 1)}s; "
                        + $"your framing holds until {Py.F(spanEnd, 1)}s.",
                    spanWords.Length > 0
                        ? $"Words spoken in this span: \"{spanWords}\""
                        : "No speech in this span — frame the visible action.",
                }),
            },
            new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = $"data:image/jpeg;base64,{frameB64}",
                },
            },
        };
    }

    private static JsonObject RequestFraming(ChatProvider provider, JsonArray content)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = REFRAME_SYSTEM_PROMPT },
                new JsonObject { ["role"] = "user", ["content"] = content.DeepClone() },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "framing",
                    ["schema"] = FrameResponseSchema(),
                },
            },
        };
        provider.ApplyRequestOptions(body);

        JsonObject response;
        using (var client = provider.Client(timeoutSeconds: 120.0))
        {
            response = client.ChatCompletions(body);
        }

        var contentText = response["choices"] is JsonArray choices && choices.Count > 0
            ? JsonUtil.StrOrNull(choices[0]?["message"]?["content"])
            : null;
        if (string.IsNullOrEmpty(contentText))
            throw new PipelineError($"{provider.Label} framing response did not include content");
        return Llm.JsonFromText(contentText);
    }

    /// <summary>One frame just after the clip start and just after each cut, plus one
    /// every frameIntervalSeconds (periodic frames next to a cut frame are
    /// skipped), capped at MAX_SAMPLE_FRAMES by evenly thinning (the opener
    /// stays).</summary>
    public static List<double> SampleTimes(
        double durationSeconds,
        IReadOnlyList<double> sceneCuts,
        double frameIntervalSeconds = Defaults.DEFAULT_REFRAME_FRAME_INTERVAL_SECONDS)
    {
        var times = new List<double> { Math.Min(0.2, Math.Max(0.0, durationSeconds / 2)) };
        foreach (var cut in sceneCuts.OrderBy(cut => cut))
        {
            var atSeconds = cut + 0.3;
            if (0.5 < atSeconds && atSeconds < durationSeconds - 0.1)
                times.Add(Py.Round(atSeconds, 2));
        }
        if (frameIntervalSeconds > 0)
        {
            for (var atSeconds = frameIntervalSeconds;
                 atSeconds < durationSeconds - 0.1;
                 atSeconds += frameIntervalSeconds)
            {
                var candidate = atSeconds;
                if (times.All(existing =>
                        Math.Abs(candidate - existing) >= PERIODIC_FRAME_MIN_GAP_SECONDS))
                    times.Add(Py.Round(candidate, 2));
            }
        }
        times.Sort();
        if (times.Count > MAX_SAMPLE_FRAMES)
        {
            var rest = times.Skip(1).ToList();
            var step = (double)rest.Count / (MAX_SAMPLE_FRAMES - 1);
            var thinned = new List<double> { times[0] };
            for (var i = 0; i < MAX_SAMPLE_FRAMES - 1; i++)
                thinned.Add(rest[(int)(i * step)]);
            times = thinned;
        }
        return times;
    }

    /// <summary>Sorted, de-jittered keyframes with the first forced to 0 seconds.
    /// Falls back to a single wide framing (the whole 16:9 fitted with blurred
    /// padding) when nothing usable comes back.</summary>
    public static List<JsonObject> ValidateKeyframes(JsonNode? raw, double durationSeconds)
    {
        var keyframes = new List<JsonObject>();
        foreach (var rawItem in raw as JsonArray ?? new JsonArray())
        {
            if (rawItem is not JsonObject item) continue;
            if (!JsonUtil.TryDouble(
                    item.TryGetPropertyValue("start_seconds", out var startNode) ? startNode : null,
                    out var start))
                continue;
            var wide = JsonUtil.Truthy(item.TryGetPropertyValue("wide", out var wideNode)
                ? wideNode
                : null);
            double center;
            if (wide)
            {
                center = 0.5;
            }
            else
            {
                if (!JsonUtil.TryDouble(
                        item.TryGetPropertyValue("center_x", out var centerNode) ? centerNode : null,
                        out var centerValue))
                    continue;
                center = Math.Min(1.0, Math.Max(0.0, centerValue));
            }
            if (start < durationSeconds)
            {
                keyframes.Add(new JsonObject
                {
                    ["start_seconds"] = Math.Max(0.0, Py.Round(start, 2)),
                    ["center_x"] = Py.Round(center, 3),
                    ["wide"] = wide,
                });
            }
        }

        keyframes = keyframes.OrderBy(k => JsonUtil.Double(k["start_seconds"])).ToList();
        if (keyframes.Count == 0)
        {
            return new List<JsonObject>
            {
                new() { ["start_seconds"] = 0.0, ["center_x"] = 0.5, ["wide"] = true },
            };
        }

        keyframes[0]["start_seconds"] = 0.0;
        var kept = new List<JsonObject> { keyframes[0] };
        foreach (var keyframe in keyframes.Skip(1))
        {
            var previous = kept[^1];
            if (JsonUtil.Double(keyframe["start_seconds"]) - JsonUtil.Double(previous["start_seconds"])
                < MIN_KEYFRAME_SPACING_SECONDS)
                continue;
            var keyframeWide = JsonUtil.Truthy(keyframe["wide"]);
            var previousWide = JsonUtil.Truthy(previous["wide"]);
            if (keyframeWide == previousWide && (
                    keyframeWide
                    || Math.Abs(JsonUtil.Double(keyframe["center_x"])
                        - JsonUtil.Double(previous["center_x"])) < MIN_CENTER_DELTA))
                continue;
            kept.Add(keyframe);
        }
        return kept;
    }

    /// <summary>Overlay enable expressions for the square-crop and wide framings, from
    /// the keyframes' spans (the last span runs to the end of the clip).</summary>
    public static (string CropEnable, string WideEnable) ModeEnables(
        IReadOnlyList<JsonObject> keyframes)
    {
        var cropSpans = new List<string>();
        var wideSpans = new List<string>();
        for (var i = 0; i < keyframes.Count; i++)
        {
            var keyframe = keyframes[i];
            var next = i + 1 < keyframes.Count ? keyframes[i + 1] : null;
            var end = next is null ? "1e9" : Py.G(JsonUtil.Double(next["start_seconds"]));
            var span = $"between(t,{Py.G(JsonUtil.Double(keyframe["start_seconds"]))},{end})";
            (JsonUtil.Truthy(keyframe.TryGetPropertyValue("wide", out var wide) ? wide : null)
                ? wideSpans
                : cropSpans).Add(span);
        }
        return (
            cropSpans.Count > 0 ? string.Join("+", cropSpans) : "0",
            wideSpans.Count > 0 ? string.Join("+", wideSpans) : "0");
    }

    /// <summary>Piecewise-constant ffmpeg crop x expression (pixels) for a full-height
    /// square crop. Positions are clamped so the square stays inside the frame.</summary>
    public static string CropXExpression(
        IReadOnlyList<JsonObject> keyframes, int sourceWidth, int sourceHeight)
    {
        var cropWidth = Math.Min(sourceWidth, sourceHeight);
        var maxX = sourceWidth - cropWidth;
        var positions = keyframes
            .Select(keyframe => Math.Min(maxX, Math.Max(0,
                (int)Math.Round(
                    JsonUtil.Double(keyframe["center_x"]) * sourceWidth - cropWidth / 2.0,
                    MidpointRounding.ToEven))))
            .ToList();
        var expression = positions[^1].ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = keyframes.Count - 2; i >= 0; i--)
        {
            var keyframe = keyframes[i + 1];
            var position = positions[i];
            expression =
                $"if(lt(t,{Py.G(JsonUtil.Double(keyframe["start_seconds"]))}),{position},{expression})";
        }
        return expression;
    }

    /// <summary>Render the blur-pad vertical: blurred zoom-fill canvas with the sharp
    /// framing overlaid at full width — the square crop jumping between keyframe
    /// positions, or the whole 16:9 frame fitted to the width on wide spans.</summary>
    public static void RenderVertical(
        string sourcePath, string outputPath, IReadOnlyList<JsonObject> keyframes)
    {
        var (sourceWidth, sourceHeight) = VideoDimensions(sourcePath);
        var cropSize = Math.Min(sourceWidth, sourceHeight);
        var xExpression = CropXExpression(
            keyframes, sourceWidth: sourceWidth, sourceHeight: sourceHeight);
        var (cropEnable, wideEnable) = ModeEnables(keyframes);
        var filtergraph =
            "[0:v]split=3[bg][fgc][fgw];"
            // Stretch the WHOLE frame to fill (not cover+crop): a center crop of a
            // dark frame blurs to plain black, while the full frame always carries
            // the shot's palette — and heavy blur hides the distortion.
            //
            // setsar=1 on every branch: clip renders carry a near-1 SAR (720x406
            // storage with SAR 406:405 preserving DAR 16:9), and without the reset
            // ffmpeg stamps a 16:9 DAR onto the 720x1280 output — players then
            // squish the whole vertical into a letterboxed 16:9 band.
            + $"[bg]scale={CANVAS_WIDTH}:{CANVAS_HEIGHT},setsar=1,"
            + $"gblur=sigma={BLUR_SIGMA},eq=brightness=-0.06[b];"
            + $"[fgc]crop=w={cropSize}:h={cropSize}:x='{xExpression}':y=0,"
            + $"scale={CANVAS_WIDTH}:{CANVAS_WIDTH},setsar=1[fc];"
            + $"[fgw]scale={CANVAS_WIDTH}:-2,setsar=1[fw];"
            + $"[b][fc]overlay=0:(H-h)/2:enable='{cropEnable}'[bc];"
            + $"[bc][fw]overlay=0:(H-h)/2:enable='{wideEnable}',setsar=1[v]";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var (code, _, stderr) = Proc.Run(new List<string>
        {
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            sourcePath,
            "-filter_complex",
            filtergraph,
            "-map",
            "[v]",
            "-map",
            "0:a?",
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            // The vertical is the short-form deliverable and a second-generation
            // encode of the clip render, so it gets the lowest CRF in the chain.
            "-crf",
            "20",
            "-c:a",
            "copy",
            "-movflags",
            "+faststart",
            outputPath,
        });
        if (code != 0)
        {
            var details = stderr.Trim();
            throw new PipelineError(
                details.Length > 0 ? details : "ffmpeg failed while rendering the vertical clip");
        }
    }

    public static (int Width, int Height) VideoDimensions(string path)
    {
        var (code, stdout, stderr) = Proc.Run(new List<string>
        {
            "ffprobe",
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=width,height",
            "-of",
            "csv=p=0",
            path,
        });
        var firstLine = stdout.Trim().Split('\n').FirstOrDefault() ?? "";
        var parts = firstLine.Split(',');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var width)
            && int.TryParse(parts[1], out var height))
            return (width, height);
        var details = stderr.Trim();
        throw new PipelineError(
            details.Length > 0 ? details : $"Could not read video dimensions from {path}");
    }
}
