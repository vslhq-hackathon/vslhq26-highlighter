using System.Text.Json.Nodes;
using Highlighter.Pipeline;
using Xunit;

namespace Highlighter.Pipeline.Tests;

/// <summary>Port of pipeline/tests/test_reframe.py.</summary>
public class ReframeTests
{
    private static void AssertKeyframe(
        JsonObject keyframe, double startSeconds, double centerX, bool wide)
    {
        Assert.Equal(startSeconds, JsonUtil.Double(keyframe["start_seconds"]));
        Assert.Equal(centerX, JsonUtil.Double(keyframe["center_x"]));
        Assert.Equal(wide, JsonUtil.Truthy(keyframe["wide"]));
    }

    [Fact]
    public void ValidateFallsBackToWideFraming()
    {
        foreach (var raw in new JsonNode?[]
                 {
                     null,
                     new JsonArray(),
                     new JsonArray
                     {
                         new JsonObject { ["start_seconds"] = "x", ["center_x"] = null },
                     },
                 })
        {
            var keyframes = Reframe.ValidateKeyframes(raw, 30.0);
            Assert.Single(keyframes);
            AssertKeyframe(keyframes[0], 0.0, 0.5, true);
        }
    }

    [Fact]
    public void SpanWordsTextJoinsOverlappingWordsInOrder()
    {
        var words = new JsonArray
        {
            new JsonObject { ["punctuated_word"] = "So", ["start"] = 0.1, ["end"] = 0.3 },
            new JsonObject { ["punctuated_word"] = "like,", ["start"] = 0.3, ["end"] = 0.6 },
            new JsonObject { ["word"] = "name?", ["start"] = 4.8, ["end"] = 5.2 },
            new JsonObject { ["punctuated_word"] = "Ariana.", ["start"] = 6.0, ["end"] = 6.4 },
        };
        // Word ending exactly at spanStart is excluded; overlap at the end is kept.
        Assert.Equal("like, name?", Reframe.SpanWordsText(words, 0.3, 5.0));
        Assert.Equal("Ariana.", Reframe.SpanWordsText(words, 5.5, 10.0));
        Assert.Equal("", Reframe.SpanWordsText(words, 20.0, 25.0));
        Assert.Equal("", Reframe.SpanWordsText(null, 0.0, 5.0));
    }

    [Fact]
    public void SpanWordsTextSkipsUntimedWordsAndCapsLength()
    {
        var words = new JsonArray
        {
            new JsonObject { ["punctuated_word"] = "ghost" },
            new JsonObject { ["punctuated_word"] = "kept", ["start"] = 1.0, ["end"] = 1.2 },
        };
        for (var i = 0; i < 400; i++)
        {
            words.Add(new JsonObject
            {
                ["punctuated_word"] = "padding",
                ["start"] = 2.0 + i * 0.01,
                ["end"] = 2.1 + i * 0.01,
            });
        }
        var text = Reframe.SpanWordsText(words, 0.0, 30.0);
        Assert.StartsWith("kept padding", text);
        Assert.True(text.Length <= 1200);
    }

    [Fact]
    public void ValidateForcesFirstKeyframeToZeroAndSorts()
    {
        var keyframes = Reframe.ValidateKeyframes(
            new JsonArray
            {
                new JsonObject { ["start_seconds"] = 12.0, ["center_x"] = 0.8 },
                new JsonObject { ["start_seconds"] = 0.7, ["center_x"] = 0.3 },
            },
            30.0);
        AssertKeyframe(keyframes[0], 0.0, 0.3, false);
        AssertKeyframe(keyframes[1], 12.0, 0.8, false);
    }

    [Fact]
    public void ValidateDropsJitterKeyframes()
    {
        var keyframes = Reframe.ValidateKeyframes(
            new JsonArray
            {
                new JsonObject { ["start_seconds"] = 0.0, ["center_x"] = 0.3 },
                // Too soon after the previous keyframe.
                new JsonObject { ["start_seconds"] = 0.8, ["center_x"] = 0.9 },
                // Move too small to matter.
                new JsonObject { ["start_seconds"] = 5.0, ["center_x"] = 0.31 },
                new JsonObject { ["start_seconds"] = 9.0, ["center_x"] = 0.7 },
            },
            30.0);
        Assert.Equal(2, keyframes.Count);
        AssertKeyframe(keyframes[0], 0.0, 0.3, false);
        AssertKeyframe(keyframes[1], 9.0, 0.7, false);
    }

    [Fact]
    public void ValidateDropsKeyframesPastTheClipAndClampsCenter()
    {
        var keyframes = Reframe.ValidateKeyframes(
            new JsonArray
            {
                new JsonObject { ["start_seconds"] = 0.0, ["center_x"] = 1.7 },
                new JsonObject { ["start_seconds"] = 45.0, ["center_x"] = 0.5 },
            },
            30.0);
        Assert.Single(keyframes);
        AssertKeyframe(keyframes[0], 0.0, 1.0, false);
    }

    [Fact]
    public void ValidateWideNormalizesCenterAndMergesConsecutiveWides()
    {
        var keyframes = Reframe.ValidateKeyframes(
            new JsonArray
            {
                new JsonObject { ["start_seconds"] = 0.0, ["center_x"] = 0.9, ["wide"] = true },
                // Redundant: still wide.
                new JsonObject { ["start_seconds"] = 6.0, ["center_x"] = 0.1, ["wide"] = true },
                new JsonObject { ["start_seconds"] = 12.0, ["center_x"] = 0.7, ["wide"] = false },
            },
            30.0);
        Assert.Equal(2, keyframes.Count);
        AssertKeyframe(keyframes[0], 0.0, 0.5, true);
        AssertKeyframe(keyframes[1], 12.0, 0.7, false);
    }

    [Fact]
    public void ValidateKeepsModeChangesWithIdenticalCenters()
    {
        var keyframes = Reframe.ValidateKeyframes(
            new JsonArray
            {
                new JsonObject { ["start_seconds"] = 0.0, ["center_x"] = 0.5, ["wide"] = false },
                new JsonObject { ["start_seconds"] = 5.0, ["center_x"] = 0.5, ["wide"] = true },
                new JsonObject { ["start_seconds"] = 10.0, ["center_x"] = 0.5, ["wide"] = false },
            },
            30.0);
        Assert.Equal(
            new[] { false, true, false },
            keyframes.Select(k => JsonUtil.Truthy(k["wide"])).ToArray());
    }

    [Fact]
    public void ModeEnablesSplitSpansBetweenFramings()
    {
        var (cropEnable, wideEnable) = Reframe.ModeEnables(new List<JsonObject>
        {
            new() { ["start_seconds"] = 0.0, ["center_x"] = 0.3, ["wide"] = false },
            new() { ["start_seconds"] = 5.0, ["center_x"] = 0.5, ["wide"] = true },
            new() { ["start_seconds"] = 12.5, ["center_x"] = 0.7, ["wide"] = false },
        });
        Assert.Equal("between(t,0,5)+between(t,12.5,1e9)", cropEnable);
        Assert.Equal("between(t,5,12.5)", wideEnable);
    }

    [Fact]
    public void ModeEnablesWithoutWidesDisablesTheWideOverlay()
    {
        var (cropEnable, wideEnable) = Reframe.ModeEnables(new List<JsonObject>
        {
            new() { ["start_seconds"] = 0.0, ["center_x"] = 0.5, ["wide"] = false },
        });
        Assert.Equal("between(t,0,1e9)", cropEnable);
        Assert.Equal("0", wideEnable);
    }

    [Fact]
    public void CropExpressionSingleKeyframeIsAConstant()
    {
        var expression = Reframe.CropXExpression(
            new List<JsonObject> { new() { ["start_seconds"] = 0.0, ["center_x"] = 0.5 } },
            sourceWidth: 1280,
            sourceHeight: 720);
        Assert.Equal("280", expression); // 0.5 * 1280 - 720/2
    }

    [Fact]
    public void CropExpressionBuildsNestedPiecewise()
    {
        var expression = Reframe.CropXExpression(
            new List<JsonObject>
            {
                new() { ["start_seconds"] = 0.0, ["center_x"] = 0.3 },
                new() { ["start_seconds"] = 5.0, ["center_x"] = 0.7 },
                new() { ["start_seconds"] = 9.5, ["center_x"] = 0.5 },
            },
            sourceWidth: 1280,
            sourceHeight: 720);
        Assert.Equal("if(lt(t,5),24,if(lt(t,9.5),536,280))", expression);
    }

    [Fact]
    public void CropExpressionClampsPositionsIntoFrame()
    {
        var expression = Reframe.CropXExpression(
            new List<JsonObject>
            {
                new() { ["start_seconds"] = 0.0, ["center_x"] = 0.0 },
                new() { ["start_seconds"] = 5.0, ["center_x"] = 1.0 },
            },
            sourceWidth: 1280,
            sourceHeight: 720);
        Assert.Equal("if(lt(t,5),0,560)", expression); // 560 = 1280 - 720
    }

    [Fact]
    public void SampleTimesOpenerOnlyWhenPeriodicDisabled()
    {
        Assert.Equal(
            new List<double> { 0.2 },
            Reframe.SampleTimes(58.0, new List<double>(), frameIntervalSeconds: 0));
    }

    [Fact]
    public void SampleTimesPeriodicFramesByDefault()
    {
        Assert.Equal(
            new List<double> { 0.2, 5.0, 10.0, 15.0 },
            Reframe.SampleTimes(18.0, new List<double>()));
    }

    [Fact]
    public void SampleTimesPeriodicSkipsFramesNextToACutFrame()
    {
        // The cut frame at 6.3 shadows the periodic frame at 5.0.
        Assert.Equal(
            new List<double> { 0.2, 6.3, 10.0, 15.0 },
            Reframe.SampleTimes(18.0, new List<double> { 6.0 }));
    }

    [Fact]
    public void SampleTimesCappedWithOpenerKept()
    {
        var cuts = Enumerable.Range(0, 29).Select(i => (double)(2 + i * 2)).ToList();
        var times = Reframe.SampleTimes(60.0, cuts);
        Assert.Equal(Reframe.MAX_SAMPLE_FRAMES, times.Count);
        Assert.Equal(0.2, times[0]);
        for (var i = 1; i < times.Count - 1; i++)
            Assert.True(times[i] < times[i + 1]);
    }
}
