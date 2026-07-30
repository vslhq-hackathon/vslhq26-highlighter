using Highlighter.Api.Contracts;
using Highlighter.Api.Services;
using Xunit;

namespace Highlighter.Api.Tests;

public class WorkerArgsTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Ingest_MinimalRequest_AlwaysPassesMaxChunksAndChunkSeconds()
    {
        var argv = WorkerArgs.Ingest(Id, new CreateProjectRequest("https://x", "short"));

        Assert.Equal(
        [
            "ingest",
            "--project-id", Id.ToString(),
            "--pipeline", "short",
            "--max-chunks", "0",
            "--chunk-seconds", "90",
        ], argv);
    }

    [Fact]
    public void Ingest_InstructionsStayOneArgumentWithSpacesIntact()
    {
        var request = new CreateProjectRequest("https://x", "both",
            Instructions: "focus on the debate segments, keep it documentary-paced",
            TargetMinutes: "7-15", MaxChunks: 2, ChunkSeconds: 60,
            NoResearch: true, NoThumbnails: true);

        var argv = WorkerArgs.Ingest(Id, request);

        Assert.Equal(
        [
            "ingest",
            "--project-id", Id.ToString(),
            "--pipeline", "both",
            "--max-chunks", "2",
            "--chunk-seconds", "60",
            "--target-minutes", "7-15",
            "--instructions", "focus on the debate segments, keep it documentary-paced",
            "--no-research",
            "--no-thumbnails",
        ], argv);
    }

    [Fact]
    public void Ingest_PassesConcurrencyFlagsOnlyWhenConfigured()
    {
        var request = new CreateProjectRequest("https://x", "short");

        var bare = WorkerArgs.Ingest(Id, request);
        Assert.DoesNotContain("--llm-concurrency", bare);
        Assert.DoesNotContain("--transcribe-concurrency", bare);

        var tuned = WorkerArgs.Ingest(Id, request, llmConcurrency: 12, transcribeConcurrency: 3);
        Assert.Equal("12", tuned[tuned.IndexOf("--llm-concurrency") + 1]);
        Assert.Equal("3", tuned[tuned.IndexOf("--transcribe-concurrency") + 1]);
    }

    [Fact]
    public void Ingest_NeverPassesSourceUrlOrMinClipScore()
    {
        // In attach mode the project row's values win — the API must not compete.
        var argv = WorkerArgs.Ingest(Id, new CreateProjectRequest(
            "https://youtube.com/watch?v=abc", "long", MinClipScore: 0.7));

        Assert.DoesNotContain("https://youtube.com/watch?v=abc", argv);
        Assert.DoesNotContain("--min-clip-score", argv);
    }

    [Fact]
    public void Revise_IsTwoPositionals()
    {
        Assert.Equal(
            ["revise", Id.ToString(), "tighten the middle section"],
            WorkerArgs.Revise(Id, "tighten the middle section"));
    }

    [Fact]
    public void Publish_JoinsPlatformsAndAddsOptionalFlags()
    {
        var argv = WorkerArgs.Publish(Id, "longform", ["youtube", "x"],
            title: "My title", version: 2, thumbnail: "2", plain: false, dryRun: true);

        Assert.Equal(
        [
            "publish", Id.ToString(), "longform",
            "--platforms", "youtube,x",
            "--title", "My title",
            "--version", "2",
            "--thumbnail", "2",
            "--dry-run",
        ], argv);
    }

    [Fact]
    public void Publish_ClipTargetUsesFilenameVerbatim()
    {
        var argv = WorkerArgs.Publish(Id, "clip_00002_194500_261480_short.mp4",
            ["tiktok"], null, null, null, plain: true, dryRun: false);

        Assert.Equal(
        [
            "publish", Id.ToString(), "clip_00002_194500_261480_short.mp4",
            "--platforms", "tiktok",
            "--plain",
        ], argv);
    }

    [Fact]
    public void Reclip_FormatsSecondsInvariantCulture()
    {
        var argv = WorkerArgs.Reclip(Id, 12.5, 61.48, "A title", null);

        Assert.Equal(
        [
            "reclip", Id.ToString(), "12.5", "61.48",
            "--title", "A title",
        ], argv);
    }

    [Fact]
    public void Cleanup_PassesLimit()
    {
        Assert.Equal(["cleanup", "--limit", "50"], WorkerArgs.Cleanup(50));
    }

    [Fact]
    public void Research_ModeAlwaysExplicit_FocusOptional()
    {
        Assert.Equal(["research", Id.ToString(), "--mode", "long"],
            WorkerArgs.Research(Id, "long", null));
        Assert.Equal(["research", Id.ToString(), "--mode", "short", "--focus", "hooks"],
            WorkerArgs.Research(Id, "short", "hooks"));
    }

    [Fact]
    public void Thumbnails_GenerateVsSelect()
    {
        Assert.Equal(["thumbnails", Id.ToString(), "--prompt", "bold text"],
            WorkerArgs.Thumbnails(Id, "bold text", null, null));
        Assert.Equal(["thumbnails", Id.ToString(), "--version", "2", "--select", "3"],
            WorkerArgs.Thumbnails(Id, null, 2, 3));
    }

    [Fact]
    public void Reformat_SquareWithCaptions()
    {
        Assert.Equal(
            ["reformat", Id.ToString(), "clip_003.mp4", "--format", "square", "--captions"],
            WorkerArgs.Reformat(Id, "clip_003.mp4", "square", captions: true));
        Assert.Equal(
            ["reformat", Id.ToString(), "clip_003.mp4", "--format", "square"],
            WorkerArgs.Reformat(Id, "clip_003.mp4", "square", captions: false));
    }
}
