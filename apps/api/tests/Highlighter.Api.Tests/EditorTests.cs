using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;
using Highlighter.Api.Services;
using Xunit;

namespace Highlighter.Api.Tests;

public class EditorDocsTests
{
    private static EditorDoc Doc(params EdlSegment[] segments) =>
        EditorDocs.Default(100, []) with { Segments = segments };

    [Fact]
    public void Default_CoversWholeSource()
    {
        var doc = EditorDocs.Default(42.5, []);
        var segment = Assert.Single(doc.Segments);
        Assert.Equal(0, segment.SrcStart);
        Assert.Equal(42.5, segment.SrcEnd);
        Assert.Equal("boxed", doc.CaptionStyle);
        Assert.Null(EditorDocs.Validate(doc, 42.5));
    }

    [Fact]
    public void SeedCaptions_RebasesAndBreaksLines()
    {
        var chunk = new JsonObject
        {
            ["words"] = new JsonArray(
                Word("hello", 100.0, 100.4), Word("world", 100.5, 100.9),
                // 0.8s gap forces a new line
                Word("second", 101.7, 102.1), Word("line", 102.2, 102.6),
                // outside the clip window — dropped
                Word("outside", 130.0, 130.5)),
        };

        var captions = EditorDocs.SeedCaptions([chunk], windowStart: 100.0, windowEnd: 110.0);

        Assert.Equal(2, captions.Count);
        Assert.Equal("hello world", captions[0].Text);
        Assert.Equal(0, captions[0].Start, 3);
        Assert.Equal(0.9, captions[0].End, 3);
        Assert.Equal("second line", captions[1].Text);
        Assert.Equal(1.7, captions[1].Start, 3);
        Assert.NotNull(captions[0].Words);
        Assert.Equal(0.5, captions[0].Words![1].S, 3);

        static JsonNode Word(string text, double start, double end) => new JsonObject
        {
            ["punctuated_word"] = text,
            ["absolute_start"] = start,
            ["absolute_end"] = end,
        };
    }

    [Theory]
    [InlineData(0, 10, 1.0, null)]
    [InlineData(0, 10, 0.4, "speed")]
    [InlineData(0, 10, 2.5, "speed")]
    [InlineData(5, 4, 1.0, "before end")]
    [InlineData(0, 120, 1.0, "past the end")]
    public void Validate_SegmentRules(double start, double end, double speed, string? expectError)
    {
        var error = EditorDocs.Validate(Doc(new EdlSegment("s1", start, end, speed)), 100);
        if (expectError is null) Assert.Null(error);
        else Assert.Contains(expectError, error);
    }

    [Fact]
    public void Validate_RejectsOverlapAndDisorder()
    {
        var error = EditorDocs.Validate(
            Doc(new EdlSegment("s1", 10, 20), new EdlSegment("s2", 15, 30)), 100);
        Assert.Contains("chronological", error);
    }

    [Fact]
    public void OutputMapping_HandlesCutsAndSpeed()
    {
        // 0-10 at 1x (10s out), 20-30 at 2x (5s out) => 15s total
        var doc = Doc(new EdlSegment("a", 0, 10), new EdlSegment("b", 20, 30, 2.0));

        Assert.Equal(15, EditorDocs.OutputDuration(doc), 3);
        Assert.Equal(5, EditorDocs.SourceToOutput(doc, 5)!.Value, 3);
        Assert.Null(EditorDocs.SourceToOutput(doc, 15));          // inside the cut
        Assert.Equal(12.5, EditorDocs.SourceToOutput(doc, 25)!.Value, 3);

        // A window spanning the cut maps to its visible extent.
        var window = EditorDocs.MapWindow(doc, 8, 22);
        Assert.Equal(8, window!.Value.Start, 3);
        Assert.Equal(11, window.Value.End, 3);

        // A window entirely inside the cut disappears.
        Assert.Null(EditorDocs.MapWindow(doc, 12, 18));
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var doc = EditorDocs.Default(30, [new EdlCaption("c1", 1, 2, "hi")]) with
        {
            Transform = new EdlTransform(1.4, -0.25),
            Audio = new EdlAudio(0.9, 0.3),
            Reframe = "manual",
        };
        var restored = EditorDocs.FromJson(EditorDocs.ToJson(doc));
        // Records compare collections by reference — compare the wire form.
        Assert.Equal(EditorDocs.ToJson(doc).ToJsonString(),
            EditorDocs.ToJson(restored!).ToJsonString());
        Assert.Equal(doc.Transform, restored!.Transform);
        Assert.Equal(doc.Audio, restored.Audio);
        Assert.Equal("manual", restored.Reframe);
    }

    [Fact]
    public void Normalize_DefaultsFormatAndCaptions()
    {
        // A document persisted before formats existed omits both fields; it has
        // to keep exporting exactly as it used to (source geometry, captions on).
        var legacy = EditorDocs.FromJson(JsonNode.Parse("""
            {"v":1,"segments":[{"id":"s1","srcStart":0,"srcEnd":10}],"captions":[],
             "captionStyle":"boxed","texts":[],"markers":[],
             "transform":{"scale":1,"posX":0},"audio":{"voice":1,"music":0},"reframe":"auto"}
            """));

        Assert.NotNull(legacy);
        Assert.Equal("source", legacy!.Format);
        Assert.True(legacy.CaptionsEnabled);
        Assert.Null(EditorDocs.Validate(legacy, 10));
    }

    [Theory]
    [InlineData("source", null)]
    [InlineData("vertical", null)]
    [InlineData("square", null)]
    [InlineData("wide", null)]
    [InlineData("portrait", "format must be")]
    public void Validate_ChecksFormat(string format, string? expectError)
    {
        var doc = EditorDocs.Default(30, []) with { Format = format };
        var error = EditorDocs.Validate(doc, 30);
        if (expectError is null) Assert.Null(error);
        else Assert.Contains(expectError, error);
    }

    [Fact]
    public void SeedCaptions_AcrossStitchedSegments_LandsAtCumulativeOffsets()
    {
        // A stitched cut plays its kept source windows back to back, so the
        // second window's lines belong at the first window's duration, not at
        // their original source time.
        var chunk = new JsonObject
        {
            ["words"] = new JsonArray(
                Word("first", 30.0, 30.5), Word("window", 30.6, 31.0),
                Word("second", 120.0, 120.5), Word("window", 120.6, 121.0)),
        };
        (double Start, double End)[] segments = [(29, 87), (118, 242)];

        var captions = new List<EdlCaption>();
        double elapsed = 0;
        foreach (var (start, end) in segments)
        {
            captions.AddRange(EditorDocs.OffsetCaptions(
                EditorDocs.SeedCaptions([chunk], start, end), elapsed, captions.Count + 1));
            elapsed += end - start;
        }

        Assert.Equal(2, captions.Count);
        // 30.0 is 1s into the first window, which starts the cut.
        Assert.Equal(1.0, captions[0].Start, 3);
        // 120.0 is 2s into the second window, which starts 58s into the cut.
        Assert.Equal(60.0, captions[1].Start, 3);
        Assert.Equal(60.0, captions[1].Words![0].S, 3);
        Assert.Equal(["c1", "c2"], captions.Select(c => c.Id));

        static JsonNode Word(string text, double start, double end) => new JsonObject
        {
            ["punctuated_word"] = text,
            ["absolute_start"] = start,
            ["absolute_end"] = end,
        };
    }

    [Fact]
    public void OffsetCaptions_ShiftsWindowsAndRenumbers()
    {
        var seeded = new List<EdlCaption>
        {
            new("c1", 0, 1, "first", [new EdlWord("first", 0, 1)]),
            new("c2", 2, 3, "second", [new EdlWord("second", 2, 3)]),
        };

        var shifted = EditorDocs.OffsetCaptions(seeded, offset: 12.5, startIndex: 4);

        Assert.Equal(["c4", "c5"], shifted.Select(c => c.Id));
        Assert.Equal(12.5, shifted[0].Start, 3);
        Assert.Equal(14.5, shifted[1].Start, 3);
        Assert.Equal(12.5, shifted[0].Words![0].S, 3);
    }
}

public class EditorRendererTests
{
    private static readonly EditorRenderer.SourceInfo Vertical = new(60, 1080, 1920, HasAudio: true);

    [Fact]
    public void FilterGraph_TrimsConcatsAndFinishes()
    {
        var doc = EditorDocs.Default(60, []) with
        {
            Segments = [new EdlSegment("a", 0, 10), new EdlSegment("b", 20, 30, 1.5)],
            Audio = new EdlAudio(0.8, 0),
        };

        var graph = EditorRenderer.BuildFilterGraph(doc, Vertical, [], 1);

        Assert.Contains("[0:v]trim=start=0:end=10,setpts=(PTS-STARTPTS)/1[v0]", graph);
        Assert.Contains("atempo=1.5[a1]", graph);
        Assert.Contains("concat=n=2:v=1:a=1[vcat][acat]", graph);
        // No music: the voice chain itself must carry the mapped [aout] label.
        Assert.Contains("[acat]volume=0.8[aout]", graph);
        Assert.Contains("format=yuv420p", graph);
        Assert.DoesNotContain("amix", graph);      // no music
        Assert.DoesNotContain("crop", graph);      // reframe auto
    }

    [Fact]
    public void FilterGraph_ManualReframe_CropsAndRestoresSize()
    {
        var doc = EditorDocs.Default(60, []) with
        {
            Reframe = "manual",
            Transform = new EdlTransform(Scale: 1.5, PosX: 1.0),
        };

        var graph = EditorRenderer.BuildFilterGraph(doc, Vertical, [], 1);

        Assert.Contains("crop=w=iw/1.5:h=ih/1.5:x=(iw-iw/1.5)*1:y=(ih-ih/1.5)/2", graph);
        Assert.Contains("scale=1080:1920", graph);
    }

    [Fact]
    public void FilterGraph_MusicBed_MixesSecondInput()
    {
        var doc = EditorDocs.Default(60, []) with { Audio = new EdlAudio(1.0, 0.6) };

        var graph = EditorRenderer.BuildFilterGraph(doc, Vertical, [], 2);

        Assert.Contains("[1:a]volume=0.3[amusic]", graph);
        Assert.Contains("amix=inputs=2:duration=first", graph);

        var args = EditorRenderer.BuildRenderArgs(doc, Vertical, "in.mp4", "pad.wav", [], "out.mp4");
        Assert.Contains("-stream_loop", args);
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void FilterGraph_ChainsOverlaysWithWindows()
    {
        var doc = EditorDocs.Default(60, []);
        var overlays = new List<EditorRenderer.OverlaySpec>
        {
            new("ov_000.png", 5, 10, EditorRenderer.CaptionYExpr),
            new("ov_001.png", 12, 14, "main_h*0.3"),
        };

        var graph = EditorRenderer.BuildFilterGraph(doc, Vertical, overlays, overlayInputBase: 1);
        var args = EditorRenderer.BuildRenderArgs(doc, Vertical, "in.mp4", null, overlays, "out.mp4");

        Assert.Contains("[vbase][1:v]overlay=x=(main_w-overlay_w)/2:y=main_h*0.86-overlay_h"
            + ":enable='between(t,5,10)'[vo0]", graph);
        Assert.Contains("[vo0][2:v]overlay=x=(main_w-overlay_w)/2:y=main_h*0.3"
            + ":enable='between(t,12,14)'[vover]", graph);
        Assert.Contains("[vover]format=yuv420p[vout]", graph);
        Assert.Contains("ov_000.png", args);
        Assert.Contains("ov_001.png", args);
    }

    [Fact]
    public void PlanOverlays_MapsWindows_AndDropsCutLines()
    {
        var doc = EditorDocs.Default(60, []) with
        {
            Segments = [new EdlSegment("a", 10, 30)],
            Captions =
            [
                new EdlCaption("c1", 12, 14, "kept line"),
                new EdlCaption("c2", 0, 5, "cut away entirely"),
            ],
            Texts = [new EdlText("t1", 15, 20, "SALE", 0.3)],
        };

        var plan = EditorRenderer.PlanOverlays(doc);

        Assert.Equal(2, plan.Count);
        Assert.Equal("caption", plan[0].Kind);
        Assert.Equal(2, plan[0].Start, 3);
        Assert.Equal(4, plan[0].End, 3);
        Assert.Equal("text", plan[1].Kind);
        Assert.Equal(5, plan[1].Start, 3);
        Assert.Equal("main_h*0.3", plan[1].YExpr);
        Assert.DoesNotContain(plan, item => item.Text.Contains("cut away"));
    }

    [Fact]
    public void PlanOverlays_Karaoke_EmitsWordStates_AndDegradesPastCap()
    {
        var doc = EditorDocs.Default(60, []) with
        {
            Captions =
            [
                new EdlCaption("c1", 1, 2, "two words",
                    [new EdlWord("two", 1.0, 1.5), new EdlWord("words", 1.5, 2.0)]),
            ],
            CaptionStyle = "karaoke",
        };

        var plan = EditorRenderer.PlanOverlays(doc);
        Assert.Equal(2, plan.Count);
        Assert.Equal(1, plan[0].HighlightWords);
        Assert.Equal(2, plan[1].HighlightWords);
        Assert.Equal(1.5, plan[0].End, 3);
        Assert.Equal(1.5, plan[1].Start, 3);

        // Over the input cap, karaoke degrades to one line-level overlay.
        var degraded = EditorRenderer.PlanOverlays(doc, maxInputs: 1);
        var single = Assert.Single(degraded);
        Assert.Equal(2, single.HighlightWords);
    }

    [Fact]
    public void PlanOverlays_SkipsCaptionsWhenDisabled()
    {
        var doc = EditorDocs.Default(60, [new EdlCaption("c1", 1, 3, "spoken line")]) with
        {
            Texts = [new EdlText("t1", 2, 4, "TITLE")],
            CaptionsEnabled = false,
        };

        var plan = EditorRenderer.PlanOverlays(doc);

        var only = Assert.Single(plan);
        Assert.Equal("text", only.Kind);
    }

    [Theory]
    [InlineData("vertical", 1080, 1920)]
    [InlineData("square", 1080, 1080)]
    [InlineData("wide", 1920, 1080)]
    [InlineData("source", 1080, 1920)] // Vertical's own geometry
    public void OutputDims_FollowFormat(string format, int width, int height)
    {
        var doc = EditorDocs.Default(60, []) with { Format = format };
        Assert.Equal((width, height), EditorRenderer.OutputDims(doc, Vertical));
    }

    [Fact]
    public void FilterGraph_AutoFormat_BlurPadsIntoTarget()
    {
        // A 16:9 master delivered as 9:16: nothing may be cropped away, so the
        // frame fits inside and the margins get a blurred copy.
        var source = new EditorRenderer.SourceInfo(60, 1280, 720, HasAudio: true);
        var doc = EditorDocs.Default(60, []) with { Format = "vertical", Reframe = "auto" };

        var graph = EditorRenderer.BuildFilterGraph(doc, source, [], 1);

        Assert.Contains("[vcat]split=2[vbg][vfg]", graph);
        Assert.Contains("force_original_aspect_ratio=increase", graph);
        Assert.Contains("gblur=sigma=28", graph);
        Assert.Contains("scale=1080:1920:force_original_aspect_ratio=decrease", graph);
        Assert.Contains("[vbgb][vfgs]overlay=x=(main_w-overlay_w)/2:y=(main_h-overlay_h)/2[vbase]", graph);
    }

    [Fact]
    public void FilterGraph_ManualFormat_PansAtZoomOne()
    {
        // The old code only cropped when scale > 1, so Position X did nothing at
        // 1x. Against a target aspect it must pan on its own.
        var source = new EditorRenderer.SourceInfo(60, 1920, 1080, HasAudio: true);
        var doc = EditorDocs.Default(60, []) with
        {
            Format = "vertical",
            Reframe = "manual",
            Transform = new EdlTransform(Scale: 1.0, PosX: -1.0),
        };

        var graph = EditorRenderer.BuildFilterGraph(doc, source, [], 1);

        Assert.Contains("crop=w=min(iw\\,ih*1080/1920)/1:h=min(ih\\,iw*1920/1080)/1", graph);
        Assert.Contains(":x=(iw-(min(iw\\,ih*1080/1920)/1))*0", graph); // posX -1 => hard left
        Assert.Contains("scale=1080:1920,setsar=1", graph);
    }

    [Fact]
    public void FilterGraph_SourceFormat_IsUnchangedByTheFormatStage()
    {
        // Documents saved before formats existed must render byte-identically.
        var doc = EditorDocs.Default(60, []) with { Format = "source" };
        var graph = EditorRenderer.BuildFilterGraph(doc, Vertical, [], 1);

        Assert.Contains("[vcat]null[vbase]", graph);
        Assert.DoesNotContain("split=2", graph);
    }

    [Fact]
    public void BuildFitArgs_TargetsBitrate_AndCopiesAudio()
    {
        var args = EditorRenderer.BuildFitArgs("export.mp4", "export_fit.mp4", 1_500_000);
        Assert.Equal("export.mp4", args[args.IndexOf("-i") + 1]);
        Assert.Equal("1500000", args[args.IndexOf("-b:v") + 1]);
        Assert.Equal("1500000", args[args.IndexOf("-maxrate") + 1]);
        Assert.Equal("3000000", args[args.IndexOf("-bufsize") + 1]);
        Assert.Equal("copy", args[args.IndexOf("-c:a") + 1]);
        Assert.Equal("export_fit.mp4", args[^1]);
    }

    [Fact]
    public void CaptionRasterizer_ProducesPngs()
    {
        foreach (var style in new[] { "boxed", "plain", "karaoke" })
        {
            var png = CaptionRasterizer.RenderCaption(
                "nobody budgets for the retry storm", style, 720, 1280,
                ["nobody", "budgets", "for", "the", "retry", "storm"], 2);
            Assert.True(png.Length > 200, $"{style} png too small");
            // PNG magic bytes.
            Assert.Equal(0x89, png[0]);
            Assert.Equal((byte)'P', png[1]);
        }
        var text = CaptionRasterizer.RenderTextOverlay("EDITED IN HIGHLIGHTER", 720, 1280);
        Assert.True(text.Length > 200);
    }
}
