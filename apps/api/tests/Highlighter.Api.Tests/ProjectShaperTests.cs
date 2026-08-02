using System.Text.Json;
using System.Text.Json.Nodes;
using Highlighter.Api.Services;
using Xunit;

namespace Highlighter.Api.Tests;

public class ProjectShaperTests
{
    private static JsonObject Row(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void Summary_DerivesProgressFromProbedMinutes()
    {
        // 30 min source at 90s chunks → 20 expected; 7 stored → 35 %.
        var row = Row("""
            {
              "id": "11111111-2222-3333-4444-555555555555",
              "name": "The Deep End — Ep. 47",
              "source_type": "video",
              "source_url": "https://youtube.com/watch?v=abc",
              "status": "ingesting",
              "error": null,
              "min_clip_score": 0.5,
              "metadata": {
                "ingest": { "pipeline": "both", "source_minutes": 30, "chunk_seconds": 90 }
              },
              "created_at": "2026-07-29T10:00:00.000000+00:00",
              "updated_at": "2026-07-29T10:05:00.000000+00:00",
              "clips": [{"count": 3}],
              "transcript_chunks": [{"count": 7}],
              "longform_edits": [{"count": 0}]
            }
            """);

        var dto = ProjectShaper.Summary(row);

        Assert.Equal("both", dto.Pipeline);
        Assert.Equal(1800, dto.DurationSeconds);
        Assert.Equal(3, dto.ClipCount);
        Assert.Equal(7, dto.ChunkCount);
        Assert.True(dto.Processing);
        Assert.Equal("ingesting", dto.Progress.Stage);
        Assert.Equal(20, dto.Progress.ChunksExpected);
        // Capture owns the first 72% of the bar; the finishing phases hold the
        // rest, so 7/20 chunks reads 25.2%, not 35%.
        Assert.Equal(0.252, dto.Progress.Percent!.Value, precision: 5);
        Assert.Equal(0.5, dto.MinClipScore);
    }

    [Fact]
    public void Progress_MaxChunksCapsExpectedAndCaptureTopsOutAtItsShare()
    {
        var progress = ProjectShaper.BuildProgress(
            "ingesting", chunksStored: 3, sourceMinutes: 30, chunkSeconds: 90, maxChunks: 2);

        Assert.Equal(2, progress.ChunksExpected);
        Assert.Equal(0.72, progress.Percent!.Value, precision: 5);
    }

    [Theory]
    [InlineData("finishing", 0.75)]
    [InlineData("editing", 0.80)]
    [InlineData("stitching", 0.86)]
    [InlineData("thumbnails", 0.90)]
    [InlineData("uploading", 0.96)]
    public void Progress_TailStagesAdvancePastCapture(string stage, double expected)
    {
        // The finishing tail has no fraction to report — every phase after
        // capture used to sit at "99%" for minutes.
        var progress = ProjectShaper.BuildProgress(
            "ingesting", chunksStored: 20, sourceMinutes: 30, chunkSeconds: 90, maxChunks: 0,
            workerStage: stage);

        Assert.Equal(stage, progress.Stage);
        Assert.Equal(expected, progress.Percent!.Value, precision: 5);
    }

    [Fact]
    public void Progress_UnknownStageFallsBackToTheCaptureFraction()
    {
        // A worker older than the stage markers (or a newer one than this API).
        var progress = ProjectShaper.BuildProgress(
            "ingesting", chunksStored: 10, sourceMinutes: 30, chunkSeconds: 90, maxChunks: 0,
            workerStage: "polishing");

        Assert.Equal(0.36, progress.Percent!.Value, precision: 5);
    }

    [Fact]
    public void Progress_LivestreamTailIsDeterminateEvenWithoutAProbe()
    {
        var progress = ProjectShaper.BuildProgress(
            "ingesting", chunksStored: 40, sourceMinutes: null, chunkSeconds: 90, maxChunks: 0,
            workerStage: "stitching");

        Assert.Equal(0.86, progress.Percent!.Value, precision: 5);
    }

    [Fact]
    public void Progress_LivestreamWithoutProbeHasNullPercent()
    {
        var progress = ProjectShaper.BuildProgress(
            "ingesting", chunksStored: 12, sourceMinutes: null, chunkSeconds: 90, maxChunks: 0);

        Assert.Null(progress.Percent);
        Assert.Null(progress.ChunksExpected);
        Assert.Equal(12, progress.ChunksStored);
    }

    [Fact]
    public void Progress_CreatedRendersAsQueuedAndReadyIsComplete()
    {
        Assert.Equal("queued",
            ProjectShaper.BuildProgress("created", 0, null, 90, 0).Stage);
        Assert.Equal(1.0,
            ProjectShaper.BuildProgress("ready", 5, null, 90, 0).Percent);
    }

    [Fact]
    public void EmbedCount_HandlesAggregateAndFullRowShapes()
    {
        Assert.Equal(4, ProjectShaper.EmbedCount(JsonNode.Parse("""[{"count": 4}]""")));
        Assert.Equal(2, ProjectShaper.EmbedCount(JsonNode.Parse("""[{"id": 1}, {"id": 2}]""")));
        Assert.Equal(0, ProjectShaper.EmbedCount(JsonNode.Parse("[]")));
        Assert.Equal(0, ProjectShaper.EmbedCount(null));
        // A single full row that happens to have one column is NOT an aggregate
        // unless that column is "count".
        Assert.Equal(1, ProjectShaper.EmbedCount(JsonNode.Parse("""[{"id": 9}]""")));
    }

    [Fact]
    public void Clip_MapsRenderFilenameAndDerivedDuration()
    {
        var row = Row("""
            {
              "id": "22222222-2222-3333-4444-555555555555",
              "title": "The $40k GPU bill",
              "description": null,
              "start_seconds": 194.5,
              "end_seconds": 261.48,
              "score": 0.94,
              "status": "rendered",
              "video_url": "https://x/clip.mp4",
              "vertical_url": "https://x/clip_vertical.mp4",
              "captioned_url": null,
              "metadata": {
                "pipeline": "short",
                "thumbnail_url": "https://x/clip.jpg",
                "render": { "filename": "clip_00002_194500_261480_short.mp4" }
              },
              "created_at": "2026-07-29T10:00:00+00:00"
            }
            """);

        var dto = ProjectShaper.Clip(row);

        Assert.Equal(66.98, dto.DurationSeconds);
        Assert.Equal("short", dto.Pipeline);
        Assert.Equal("clip_00002_194500_261480_short.mp4", dto.FileName);
        Assert.Equal("https://x/clip.jpg", dto.ThumbnailUrl);
        Assert.Equal(0.94, dto.Score);
        Assert.Null(dto.CaptionedUrl);
    }

    [Fact]
    public void Longform_MapsSegmentsAndRevisionRequest()
    {
        var row = Row("""
            {
              "id": "33333333-2222-3333-4444-555555555555",
              "version": 2,
              "status": "rendered",
              "video_url": "https://x/longform_v2.mp4",
              "thumbnail_url": null,
              "duration_seconds": 903.2,
              "segments": [
                {"chunk_index": 0, "title": "Cold open", "start_seconds": 0, "end_seconds": 130.5}
              ],
              "revision": {"request": "tighten the intro", "notes": "done"},
              "created_at": "2026-07-29T11:00:00+00:00"
            }
            """);

        var dto = ProjectShaper.Longform(row);

        Assert.Equal(2, dto.Version);
        Assert.Equal("tighten the intro", dto.RevisionRequest);
        var segment = Assert.Single(dto.Segments);
        Assert.Equal(130.5, segment.EndSeconds);
    }

    [Fact]
    public void Detail_UsesFullEmbeddedArrays()
    {
        var row = Row("""
            {
              "id": "11111111-2222-3333-4444-555555555555",
              "name": "p", "source_type": "video", "source_url": "u", "status": "ready",
              "min_clip_score": 0, "metadata": {},
              "created_at": "2026-07-29T10:00:00+00:00",
              "updated_at": "2026-07-29T10:00:00+00:00",
              "clips": [
                {"id": "22222222-2222-3333-4444-555555555555", "title": "c",
                 "start_seconds": 0, "end_seconds": 10, "status": "rendered",
                 "metadata": {}, "created_at": "2026-07-29T10:00:00+00:00"}
              ],
              "longform_edits": [],
              "publications": [],
              "transcript_chunks": [{"count": 2}]
            }
            """);

        var dto = ProjectShaper.Detail(row, hasLocalMirror: true);

        Assert.Single(dto.Clips);
        Assert.Equal(1, dto.Project.ClipCount);
        Assert.Equal(2, dto.Project.ChunkCount);
        Assert.True(dto.HasLocalMirror);
    }

    [Fact]
    public void Dtos_SerializeCamelCaseOnTheWire()
    {
        var row = Row("""
            {
              "id": "11111111-2222-3333-4444-555555555555",
              "name": "p", "source_type": "video", "source_url": "u", "status": "ready",
              "min_clip_score": 0, "metadata": {},
              "created_at": "2026-07-29T10:00:00+00:00",
              "updated_at": "2026-07-29T10:00:00+00:00"
            }
            """);

        var json = JsonSerializer.Serialize(
            ProjectShaper.Summary(row), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"sourceUrl\"", json);
        Assert.Contains("\"chunksStored\"", json);
        Assert.DoesNotContain("\"source_url\"", json);
    }
}
