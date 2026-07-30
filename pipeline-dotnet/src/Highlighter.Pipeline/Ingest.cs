using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/ingest.py.
///
/// Run the Highlighter pipeline against a livestream or VOD.
///
/// Short mode (default): capture -> transcribe -> detect clip-worthy moments ->
/// render individual short-form clips. Long mode: same pipeline with a long-form
/// editor prompt, then the selected segments are stitched chronologically into one
/// long-form video. Both mode: capture, transcription, and shot detection run
/// once, feeding the short and long editorial forks in parallel — one run
/// produces the short-form clips and the long-form edit together.
///
/// Everything is recorded locally under &lt;output-root&gt;/projects/&lt;project-id&gt;/ in
/// the same shape Supabase stores it (see Records); Supabase writes happen too
/// unless --local-only is passed.</summary>
public static class Ingest
{
    // How often the worker re-reads its project row while capturing, to notice a
    // backend-requested cancellation (status 'stopping').
    private const int PROJECT_STATUS_POLL_SECONDS = 10;

    /// <summary>One editorial path over the shared capture: its scoring
    /// coordinator, research context, stitching policy, and render
    /// bookkeeping. Single modes run one fork; --pipeline both runs a short
    /// and a long fork in parallel.</summary>
    private sealed class Fork
    {
        public required string Mode; // "short" | "long"
        public required double MergeGapSeconds;
        public required double MaxClipSeconds;
        public required bool ReframeEnabled;
        public required string FilenameSuffix; // "" in single modes, "_short"/"_long" in both mode
        public JsonObject? ResearchContext;
        public ClipScoringCoordinator? Coordinator;
        // Emitter-thread state, touched only by this fork's own emitter.
        public readonly Dictionary<int, List<JsonObject>> ParkedRenders = new();
        public readonly List<JsonObject> RenderedLog = new();
        // How far this fork has progressed, published under the shared segment
        // lock. -1 means it may still need segment 0; segments are only
        // deleted once they are behind every fork's bound.
        public double RetireBound = -1;
    }

    public static void Main(string[] argv)
    {
        Config.LoadEnv();

        var args = IngestArgs.Parse(argv);
        var sourceUrl = args.SourceUrl
            ?? Config.Env("STREAM_URL", Defaults.DEFAULT_STREAM_URL);
        var pipelineMode = args.Pipeline;
        var userInstructions = string.IsNullOrEmpty(args.Instructions) ? null : args.Instructions;
        var targetLength = args.TargetMinutes;
        var chunkSeconds = args.ChunkSeconds;
        var maxChunks = args.MaxChunks;
        var streamlinkQuality = args.StreamlinkQuality;
        var deepgramModel = args.DeepgramModel;
        var llmEnabled = !args.NoLlm && !TruthyEnv("NO_LLM");
        var llmReasoningEffort = args.LlmReasoningEffort;
        var llmMarkerSeconds = args.LlmMarkerSeconds;
        var llmConcurrency = args.LlmConcurrency;
        var llmContextSeconds = args.LlmContextSeconds;
        var researchEnabled = llmEnabled && !args.NoResearch && !TruthyEnv("NO_RESEARCH");
        var localOnly = args.LocalOnly || TruthyEnv("LOCAL_ONLY");
        // Shot detection needs the archived video segments and only pays off when
        // clips are actually being selected.
        var shotsRequested =
            llmEnabled && !args.NoArchive && !args.NoShots && !TruthyEnv("NO_SHOTS");
        var shotDetector = Shots.ShotDetectorFromEnv(shotsRequested);
        var reframeInterval = args.ReframeInterval;
        var clipsBucket = Config.Env("SUPABASE_CLIPS_BUCKET", Defaults.DEFAULT_SUPABASE_CLIPS_BUCKET);
        var outputRoot = args.OutputRoot
            ?? Config.Env("OUTPUT_ROOT", Defaults.DEFAULT_OUTPUT_ROOT);
        var minClipScore = args.MinClipScore; // null -> resolved from the project row below

        // One editorial fork per output format; --pipeline both runs a short and
        // a long fork in parallel off the shared capture/transcription front end.
        var forkModes = pipelineMode == "both"
            ? new[] { "short", "long" }
            : new[] { pipelineMode };
        var noReframe = args.NoReframe || TruthyEnv("NO_REFRAME");
        var forks = forkModes.Select(mode => new Fork
        {
            Mode = mode,
            MergeGapSeconds = mode == "long"
                ? Defaults.DEFAULT_LONGFORM_MERGE_GAP_SECONDS
                : Defaults.DEFAULT_CLIP_MERGE_GAP_SECONDS,
            MaxClipSeconds = mode == "long"
                ? Defaults.DEFAULT_LONGFORM_MAX_CLIP_SECONDS
                : Defaults.DEFAULT_MAX_CLIP_SECONDS,
            // Short clips ship as blur-pad verticals; long-form output stays 16:9.
            ReframeEnabled = llmEnabled && mode == "short" && !noReframe,
            FilenameSuffix = forkModes.Length > 1 ? $"_{mode}" : "",
        }).ToList();
        var shortFork = forks.FirstOrDefault(fork => fork.Mode == "short");
        var longFork = forks.FirstOrDefault(fork => fork.Mode == "long");
        var reframeEnabled = shortFork is not null && shortFork.ReframeEnabled;
        var thumbnailsEnabled = longFork is not null
            && llmEnabled
            && !args.NoThumbnails
            && !TruthyEnv("NO_THUMBNAILS");
        if (thumbnailsEnabled && !Thumbnails.ThumbnailsConfigured())
        {
            Console.WriteLine("Thumbnail generation needs an image model configured; continuing without it.");
            thumbnailsEnabled = false;
        }
        var captionsEnabled = reframeEnabled && !args.NoCaptions && !TruthyEnv("NO_CAPTIONS");
        if (captionsEnabled && !Captions.CaptionsAvailable())
        {
            Console.WriteLine("Captions unavailable (pycaps not on PATH); shipping clean verticals only.");
            captionsEnabled = false;
        }
        // In both mode the two coordinators split the scoring budget so the total
        // number of in-flight LLM calls stays at the configured level.
        var perForkConcurrency = Math.Max(1, llmConcurrency / forks.Count);

        if (chunkSeconds <= 0)
            throw new PipelineError("CHUNK_SECONDS must be greater than 0");
        if (maxChunks < 0)
            throw new PipelineError("MAX_CHUNKS must be 0 or greater");
        if (llmMarkerSeconds <= 0)
            throw new PipelineError("LLM_MARKER_SECONDS must be greater than 0");
        if (llmConcurrency <= 0)
            throw new PipelineError("LLM_CONCURRENCY must be greater than 0");
        if (llmContextSeconds < 0)
            throw new PipelineError("LLM_CONTEXT_SECONDS must be 0 or greater");
        if (chunkSeconds <= 2 * llmContextSeconds)
        {
            // The boundary stitcher only looks one chunk back; wider context could
            // need merges across non-adjacent chunks.
            throw new PipelineError("CHUNK_SECONDS must be more than twice LLM_CONTEXT_SECONDS");
        }
        if (args.SourceType is not ("auto" or "livestream" or "video"))
            throw new PipelineError("SOURCE_TYPE must be one of: auto, livestream, video");
        if (reframeInterval < 0)
            throw new PipelineError("REFRAME_INTERVAL must be 0 or greater");
        if (minClipScore is double score && !(0 <= score && score <= 1))
            throw new PipelineError("MIN_CLIP_SCORE must be between 0 and 1");
        if (args.ProjectId is not null && localOnly)
            throw new PipelineError("PROJECT_ID cannot be combined with --local-only");

        // Attach mode: the backend pre-created the project row and launched this
        // worker with its id; use the row as the source of truth instead of
        // creating a new one.
        SupabaseClient? db = null;
        JsonObject? projectRow = null;
        ProjectRecords? records = null;
        if (args.ProjectId is not null)
        {
            db = new SupabaseClient();
            projectRow = db.GetProject(args.ProjectId);
        }

        string projectId;
        string projectDir;
        string audioDir;
        string platform;
        string sourceType;
        string name;
        string downloader;
        double? sourceMinutes = null;
        JsonObject ingestSettings;
        S3Storage? storage = null;
        string? archiveDir;
        var archivePrefix = "";
        var archivedSegments = new List<string>();
        // Local archive segments still on disk, shared across the forks; each
        // fork parks its own rendered-decisions (in Fork) until the segment(s)
        // their window needs have been archived. The gate guards mutations and
        // the per-fork retire bounds — in both mode two coordinator emitter
        // threads touch these.
        var segmentFiles = new Dictionary<int, string>();
        var segmentsGate = new object();
        // Measured container start per archive segment: copy cuts land on
        // keyframes at or after the nominal boundary, so a segment's true
        // position drifts past index * chunkSeconds — clip windows and scene
        // cuts are placed with these instead of nominal arithmetic.
        var segmentStarts = new Dictionary<int, double>();
        // Scene cuts per archive segment (absolute seconds) and every word's
        // absolute span, so boundary snapping never lands inside speech.
        var chunkCuts = new Dictionary<int, List<double>>();
        var cutsGate = new object();
        var wordSpans = new List<(double Start, double End)>();
        var spansGate = new object();
        // Word timings per chunk for caption renders. Written by the capture
        // thread before a chunk is scored, so every clip's covering chunks are
        // present by the time its render decision reaches an emitter.
        var chunkWords = new Dictionary<int, JsonArray>();
        var wordsGate = new object();
        JsonObject sourceContext;
        var stopRequested = false;
        var lastPoll = 0.0;
        var pollClock = Stopwatch.StartNew();
        var coordinators = new List<ClipScoringCoordinator>();
        var longformResult = new JsonObject();
        var signalRegistrations = new List<PosixSignalRegistration>();
        // Python runs setup and capture in two sequential try blocks; the flag
        // reproduces that: the outer catch below only handles setup failures.
        var captureStarted = false;

        // Once the row has been fetched in attach mode, every pre-capture failure
        // must be written back to it (see the catch below): a crash with no status
        // write would strand the row in 'created' forever.
        try
        {
            if (projectRow is not null)
            {
                var rowStatus = JsonUtil.StrOrNull(projectRow["status"]);
                if (rowStatus is "stopping" or "cancelled")
                {
                    db!.UpdateProjectStatusGuarded(
                        args.ProjectId!,
                        status: "cancelled",
                        whenStatusIn: new[] { "stopping", "cancelled" });
                    Console.WriteLine(
                        $"Project {args.ProjectId} was cancelled before capture started; exiting.");
                    return;
                }
                if (JsonUtil.Truthy(projectRow["source_url"]))
                    sourceUrl = JsonUtil.Str(projectRow["source_url"]);
                if (minClipScore is null)
                    minClipScore = JsonUtil.DoubleOr(projectRow["min_clip_score"], 0);
            }
            minClipScore ??= 0.0;

            (platform, var detectedType, name) = DetectSource(sourceUrl);
            sourceType = args.SourceType == "auto" ? detectedType : args.SourceType;
            // Live Twitch broadcasts are tapped with streamlink. Everything else is
            // pulled with yt-dlp -- including YouTube livestreams, since streamlink's
            // YouTube plugin has no cookie support and trips YouTube's datacenter
            // bot wall ("Sign in to confirm you're not a bot"). source_type keeps
            // driving archiving and clip rendering regardless of the downloader.
            downloader = platform == "twitch" && sourceType == "livestream"
                ? "streamlink"
                : "yt-dlp";

            Capture.AssertCapturePrerequisites(downloader);
            // Fail fast on a malformed YTDLP_COOKIES_B64 so the readable error
            // reaches the row via the pre-capture failure handling below.
            Cookies.PrepareYtdlpCookies();

            // Known length lets the long-form prompts state the keep rate as a
            // number instead of a vibe. Livestreams stay null.
            if (longFork is not null && sourceType == "video")
            {
                sourceMinutes = ProbeSourceMinutes(sourceUrl);
                if (sourceMinutes is not null && maxChunks > 0)
                    sourceMinutes = Math.Min(sourceMinutes.Value, maxChunks * chunkSeconds / 60.0);
                if (sourceMinutes is not null)
                    Console.WriteLine(
                        $"Source runs ~{Py.F(sourceMinutes.Value, 0)} min; calibrating selectivity to it.");
            }

            // Resolve the scoring model chain up front so a missing configuration
            // fails before capture and the run records what actually scores it.
            var llmProviders = llmEnabled
                ? Providers.AudioProviders(
                    title: "highlighter pipeline",
                    openrouterReasoningEffort: llmReasoningEffort)
                : null;
            var scoringBackend = llmProviders is not null
                ? Providers.ChainLabel(llmProviders)
                : null;

            ingestSettings = new JsonObject
            {
                ["pipeline"] = pipelineMode,
                ["user_instructions"] = userInstructions,
                ["target_length_minutes"] = longFork is not null ? targetLength : null,
                ["chunk_seconds"] = chunkSeconds,
                ["max_chunks"] = maxChunks,
                ["streamlink_quality"] = streamlinkQuality,
                ["transcription_backend"] = Transcribe.TranscriptionBackendLabel(),
                ["deepgram_model"] = deepgramModel,
                ["min_clip_score"] = minClipScore,
                ["source_minutes"] = sourceMinutes,
                ["shots"] = new JsonObject
                {
                    ["enabled"] = shotDetector is not null,
                    ["backend"] = shotDetector is not null ? "transnetv2" : null,
                },
                ["reframe"] = new JsonObject
                {
                    ["enabled"] = reframeEnabled,
                    ["style"] = reframeEnabled ? "blurpad-square" : null,
                    ["frame_interval_seconds"] = reframeEnabled ? reframeInterval : (double?)null,
                },
                ["thumbnails"] = new JsonObject
                {
                    ["enabled"] = thumbnailsEnabled,
                    ["model"] = thumbnailsEnabled ? Thumbnails.ThumbnailModelLabel() : null,
                },
                ["captions"] = new JsonObject
                {
                    ["enabled"] = captionsEnabled,
                    ["template"] = captionsEnabled ? Defaults.DEFAULT_CAPTION_TEMPLATE : null,
                },
                ["clip_stitching"] = forks.Count == 1
                    ? new JsonObject
                    {
                        ["merge_gap_seconds"] = forks[0].MergeGapSeconds,
                        ["max_clip_seconds"] = forks[0].MaxClipSeconds,
                    }
                    : new JsonObject(forks.Select(fork => KeyValuePair.Create(
                        fork.Mode,
                        (JsonNode?)new JsonObject
                        {
                            ["merge_gap_seconds"] = fork.MergeGapSeconds,
                            ["max_clip_seconds"] = fork.MaxClipSeconds,
                        }))),
                ["llm"] = new JsonObject
                {
                    ["enabled"] = llmEnabled,
                    ["backend"] = scoringBackend,
                    ["model"] = llmProviders is not null ? llmProviders[0].Model : null,
                    ["reasoning_effort"] = llmReasoningEffort,
                    ["marker_seconds"] = llmMarkerSeconds,
                    ["concurrency"] = llmConcurrency,
                    ["context_seconds"] = llmContextSeconds,
                },
                ["research"] = new JsonObject
                {
                    ["enabled"] = researchEnabled,
                    ["backend"] = researchEnabled ? Research.ResearchBackendLabel() : null,
                },
                ["clips"] = new JsonObject
                {
                    ["bucket"] = db is not null || !localOnly ? clipsBucket : null,
                },
            };
            if (forks.Count > 1)
                ((JsonObject)ingestSettings["llm"]!)["concurrency_per_fork"] = perForkConcurrency;

            // Optional S3 archive for livestreams (they are unrecoverable once
            // over). Local-segment archiving below works without it.
            if (sourceType == "livestream" && !args.NoArchive)
                storage = S3Storage.StorageFromEnv();

            if (localOnly)
            {
                projectId = "local-" + Guid.NewGuid().ToString("N")[..12];
                Console.WriteLine($"Started local project {projectId} (no Supabase writes)");
            }
            else if (projectRow is not null)
            {
                projectId = JsonUtil.Str(projectRow["id"]);
                var attached = db!.UpdateProjectStatusGuarded(
                    projectId,
                    status: "ingesting",
                    whenStatusIn: new[] { "created", "ingesting" },
                    metadata: JsonUtil.Merged(
                        projectRow["metadata"] as JsonObject ?? new JsonObject(),
                        new JsonObject
                        {
                            ["platform"] = platform,
                            ["ingest"] = JsonUtil.CloneObj(ingestSettings),
                        }));
                if (attached is null)
                {
                    // The backend flipped the row to stopping/cancelled between our
                    // read and this write; acknowledge and bail out before capturing.
                    db.UpdateProjectStatusGuarded(
                        projectId,
                        status: "cancelled",
                        whenStatusIn: new[] { "stopping", "cancelled" });
                    Console.WriteLine(
                        $"Project {projectId} was cancelled before capture started; exiting.");
                    return;
                }
                Console.WriteLine($"Attached to project {projectId}");
            }
            else
            {
                db = new SupabaseClient();
                projectId = db.CreateProject(
                    name: name,
                    sourceType: sourceType,
                    sourceUrl: sourceUrl,
                    metadata: new JsonObject
                    {
                        ["platform"] = platform,
                        ["ingest"] = JsonUtil.CloneObj(ingestSettings),
                    });
                Console.WriteLine($"Started project {projectId}");
            }

            projectDir = Path.Combine(outputRoot, "projects", projectId);
            audioDir = Path.Combine(projectDir, "audio");
            records = new ProjectRecords(projectDir);
            records.UpdateProject(
                ("id", projectId),
                ("name", name),
                ("source_type", sourceType),
                ("source_url", sourceUrl),
                ("status", "ingesting"),
                ("metadata", new JsonObject
                {
                    ["platform"] = platform,
                    ["ingest"] = JsonUtil.CloneObj(ingestSettings),
                }));

            bool StopRequestedPoll()
            {
                // Poll the project row (rate-limited) for a backend-requested cancel.
                if (db is null) return false;
                if (stopRequested) return true;
                var now = pollClock.Elapsed.TotalSeconds;
                if (now - lastPoll < PROJECT_STATUS_POLL_SECONDS) return false;
                lastPoll = now;
                string? status;
                try
                {
                    status = db.GetProjectStatus(projectId);
                }
                catch (Exception exc)
                {
                    Console.WriteLine($"Project status poll failed: {exc.Message}");
                    return false;
                }
                if (status is "stopping" or "cancelled")
                {
                    Console.WriteLine(
                        $"Cancellation requested (project status is '{status}'); stopping capture.");
                    stopRequested = true;
                }
                return stopRequested;
            }

            void HandleTerminationSignal(PosixSignalContext context)
            {
                context.Cancel = true;
                // Best-effort final status before dying. Guarded, so a terminal status
                // the backend wrote ('cancelled') is never overwritten.
                try
                {
                    var final = stopRequested ? "cancelled" : "failed";
                    records!.UpdateProject(
                        ("status", final),
                        ("error", stopRequested ? null : "worker terminated"));
                    if (db is not null)
                    {
                        if (stopRequested)
                        {
                            db.UpdateProjectStatusGuarded(
                                projectId,
                                status: "cancelled",
                                whenStatusIn: new[] { "stopping", "cancelled" });
                        }
                        else
                        {
                            var failed = db.UpdateProjectStatusGuarded(
                                projectId,
                                status: "failed",
                                whenStatusIn: new[] { "created", "ingesting" },
                                error: "worker terminated");
                            if (failed is null)
                            {
                                db.UpdateProjectStatusGuarded(
                                    projectId,
                                    status: "cancelled",
                                    whenStatusIn: new[] { "stopping" });
                            }
                        }
                    }
                }
                catch
                {
                }
                Environment.Exit(stopRequested ? 0 : 1);
            }

            if (!OperatingSystem.IsWindows())
            {
                signalRegistrations.Add(
                    PosixSignalRegistration.Create(PosixSignal.SIGTERM, HandleTerminationSignal));
            }
            signalRegistrations.Add(
                PosixSignalRegistration.Create(PosixSignal.SIGINT, HandleTerminationSignal));

            // Clips are cut from locally archived source segments — remote
            // re-downloads at render time proved fragile (broken HLS seeking,
            // sources deleted mid-job) — so archiving is on unless disabled for a
            // transcription-only run. Livestream segments are additionally copied
            // to S3 when S3_BUCKET is set; local copies are retired as the emitter
            // moves past them either way.
            archiveDir = args.NoArchive ? null : Path.Combine(projectDir, "source");
            archivePrefix = $"projects/{projectId}/source";
            sourceContext = new JsonObject
            {
                ["platform"] = platform,
                ["source_type"] = sourceType,
                ["name"] = name,
                ["source_url"] = sourceUrl,
            };

            if (researchEnabled)
            {
                // One specialized research call per fork: short researches clip
                // formats and hooks, long researches structure and retention.
                foreach (var fork in forks)
                {
                    Console.WriteLine(
                        $"Researching {fork.Mode}-form content context "
                        + $"({Research.ResearchBackendLabel()})");
                    try
                    {
                        fork.ResearchContext = Research.ResearchContentContext(
                            sourceContext: JsonUtil.CloneObj(sourceContext),
                            pipelineMode: fork.Mode,
                            userInstructions: userInstructions);
                        // research.json is the long/sole fork's; a combined run's
                        // short fork lands in research_short.json.
                        records.WriteResearch(
                            fork.ResearchContext,
                            mode: forks.Count > 1 && fork.Mode == "short" ? "short" : null);
                        var sourceCount =
                            (fork.ResearchContext["sources"] as JsonArray)?.Count ?? 0;
                        Console.WriteLine(
                            $"Cached {fork.Mode}-form content research with {sourceCount} source(s)");
                    }
                    catch (Exception exc)
                    {
                        // Research is context, not a dependency; never kill a run on it.
                        Console.WriteLine(
                            $"Content research for the {fork.Mode} fork failed "
                            + $"(continuing without it): {exc.Message}");
                    }
                }
            }

            List<double> CutsNearWindowLocked(double startSeconds, double endSeconds)
            {
                lock (cutsGate)
                {
                    return CutsNearWindow(chunkCuts, chunkSeconds, startSeconds, endSeconds);
                }
            }

            List<(double Start, double End)> WordSpansSnapshot()
            {
                lock (spansGate)
                {
                    return wordSpans.ToList();
                }
            }

            void EmitDecision(Fork fork, JsonObject decision)
            {
                // Handle one stitched decision, in chunk order (fork emitter thread).
                if (JsonUtil.Truthy(decision["is_clip_worthy"])
                    && JsonUtil.Double(decision["score"]) < minClipScore.Value)
                {
                    // Below the project's score bar: record the decision locally but
                    // do not render it or insert a clips row.
                    Console.WriteLine(
                        "Dropping clip candidate "
                        + $"{JsonUtil.Str(decision["chunk_index"])}: score {JsonUtil.Str(decision["score"])} is below "
                        + $"the minimum clip score {Py.S(minClipScore.Value)}");
                    decision = JsonUtil.With(decision,
                        ("is_clip_worthy", false),
                        ("reason",
                            $"Score {JsonUtil.Str(decision["score"])} is below the minimum clip score "
                            + $"{Py.S(minClipScore.Value)}: {JsonUtil.Str(decision["reason"])}"));
                }

                if (!JsonUtil.Truthy(decision["is_clip_worthy"]))
                {
                    StoreClipDecision(
                        db: db, records: records!, projectId: projectId, decision: decision,
                        pipeline: fork.Mode);
                    return;
                }

                // The model picked the moment; scene cuts pick the frame. Snap
                // before segment selection so the render uses the final window.
                var nearbyCuts = CutsNearWindowLocked(
                    JsonUtil.Double(decision["start_seconds"]),
                    JsonUtil.Double(decision["end_seconds"]));
                if (nearbyCuts.Count > 0)
                {
                    var (snappedStart, snappedEnd, snapInfo) = Shots.SnapWindow(
                        JsonUtil.Double(decision["start_seconds"]),
                        JsonUtil.Double(decision["end_seconds"]),
                        cuts: nearbyCuts,
                        wordSpans: WordSpansSnapshot());
                    if (snapInfo is not null)
                    {
                        Console.WriteLine(
                            $"Snapped clip candidate {JsonUtil.Str(decision["chunk_index"])} to scene cuts: "
                            + $"{JsonUtil.Str(snapInfo["original_start"])}s-{JsonUtil.Str(snapInfo["original_end"])}s -> "
                            + $"{Py.S(snappedStart)}s-{Py.S(snappedEnd)}s");
                        decision = JsonUtil.With(decision,
                            ("start_seconds", snappedStart),
                            ("end_seconds", snappedEnd),
                            ("boundary_snap", snapInfo));
                    }
                }

                if (archiveDir is null)
                {
                    StoreClipDecision(
                        db: db,
                        records: records!,
                        projectId: projectId,
                        decision: decision,
                        pipeline: fork.Mode,
                        renderResult: new JsonObject
                        {
                            ["status"] = "failed",
                            ["error"] = "Clip rendering requires source archiving (run without --no-archive).",
                        });
                    return;
                }
                TryRenderFromSegments(fork, decision);
            }

            double SegmentStart(int index)
            {
                lock (segmentsGate)
                {
                    return MeasuredSegmentStart(segmentStarts, index, chunkSeconds);
                }
            }

            List<int> NeededSegments(JsonObject decision)
            {
                var (first, last) = WindowSegmentRange(
                    JsonUtil.Double(decision["start_seconds"]),
                    JsonUtil.Double(decision["end_seconds"]),
                    chunkSeconds,
                    SegmentStart);
                return Enumerable.Range(first, last - first + 1).ToList();
            }

            void TryRenderFromSegments(Fork fork, JsonObject decision)
            {
                // Render now when every needed segment is archived; park otherwise
                // (fork emitter thread). Snapshot the segment paths in one locked
                // block: writers mutate segmentFiles under segmentsGate.
                var needed = NeededSegments(decision);
                List<int> missing;
                List<string>? segmentPaths = null;
                lock (segmentsGate)
                {
                    missing = needed.Where(index => !segmentFiles.ContainsKey(index)).ToList();
                    if (missing.Count == 0)
                        segmentPaths = needed.Select(index => segmentFiles[index]).ToList();
                }
                if (missing.Count > 0)
                {
                    if (!fork.ParkedRenders.TryGetValue(missing.Max(), out var parked))
                        fork.ParkedRenders[missing.Max()] = parked = new List<JsonObject>();
                    parked.Add(decision);
                    Console.WriteLine(
                        "Parked clip candidate "
                        + $"{JsonUtil.Str(decision["chunk_index"])} until segment(s) "
                        + $"[{string.Join(", ", missing)}] are archived");
                    return;
                }
                RenderAndStoreClip(
                    db: db,
                    records: records!,
                    projectId: projectId,
                    projectDir: projectDir,
                    clipsBucket: clipsBucket,
                    decision: decision,
                    segmentPaths: segmentPaths!,
                    firstSegmentStartSeconds: SegmentStart(needed[0]),
                    renderedLog: fork.RenderedLog,
                    pipeline: fork.Mode,
                    filenameSuffix: fork.FilenameSuffix,
                    reframeEnabled: fork.ReframeEnabled,
                    reframeInterval: reframeInterval,
                    sceneCuts: fork.ReframeEnabled
                        ? CutsNearWindowLocked(
                            JsonUtil.Double(decision["start_seconds"]),
                            JsonUtil.Double(decision["end_seconds"]))
                        : null,
                    researchContext: fork.ResearchContext,
                    captionsEnabled: captionsEnabled && fork.ReframeEnabled,
                    clipWords: captionsEnabled && fork.ReframeEnabled
                        ? WordsInWindowLocked(
                            JsonUtil.Double(decision["start_seconds"]),
                            JsonUtil.Double(decision["end_seconds"]))
                        : null);
            }

            JsonArray WordsInWindowLocked(double startSeconds, double endSeconds)
            {
                lock (wordsGate)
                {
                    return WordsInWindow(chunkWords, chunkSeconds, startSeconds, endSeconds);
                }
            }

            void RegisterSegment(Fork fork, int segmentIndex, string segmentPath)
            {
                // Record a completed archive segment (every fork registers the
                // same path) and wake this fork's parked renders (fork emitter
                // thread).
                lock (segmentsGate)
                {
                    segmentFiles[segmentIndex] = segmentPath;
                }
                foreach (var key in fork.ParkedRenders.Keys.Where(k => k <= segmentIndex)
                             .OrderBy(k => k).ToList())
                {
                    var decisions = fork.ParkedRenders[key];
                    fork.ParkedRenders.Remove(key);
                    foreach (var decision in decisions)
                    {
                        try
                        {
                            TryRenderFromSegments(fork, decision);
                        }
                        catch (Exception exc)
                        {
                            // One bad decision must not drop the rest of the batch.
                            Console.WriteLine(
                                "Rendering parked clip candidate "
                                + $"{JsonUtil.Str(decision["chunk_index"])} failed: {exc.Message}");
                        }
                    }
                }
            }

            void RetireStaleSegments(Fork fork, int chunkIndex, double? minHeldStart)
            {
                // Delete local segment copies that no fork can reference anymore.
                // Each fork publishes its own bound; only segments behind every
                // fork's bound are dropped (fork emitter threads).
                if (archiveDir is null) return;
                var bound = (double)(chunkIndex - 1);
                if (minHeldStart is double heldStart)
                    bound = Math.Min(bound, (int)(heldStart / chunkSeconds) - 1);
                foreach (var decisions in fork.ParkedRenders.Values)
                    foreach (var decision in decisions)
                        bound = Math.Min(bound,
                            (int)(JsonUtil.Double(decision["start_seconds"]) / chunkSeconds) - 1);
                var stale = new List<string>();
                lock (segmentsGate)
                {
                    fork.RetireBound = bound;
                    var sharedBound = forks.Min(f => f.RetireBound);
                    foreach (var index in segmentFiles.Keys.Where(i => i <= sharedBound).ToList())
                    {
                        stale.Add(segmentFiles[index]);
                        segmentFiles.Remove(index);
                    }
                }
                foreach (var path in stale)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (IOException exc)
                    {
                        Console.WriteLine($"Could not delete local segment {path}: {exc.Message}");
                    }
                }
            }

            void FinishLiveRenders(Fork fork)
            {
                // Fail parked decisions whose segments never arrived (fork emitter
                // thread, once at the end); the shared segment copies are dropped
                // on the main thread once every fork has drained.
                foreach (var key in fork.ParkedRenders.Keys.OrderBy(k => k).ToList())
                {
                    foreach (var decision in fork.ParkedRenders[key])
                    {
                        try
                        {
                            StoreClipDecision(
                                db: db,
                                records: records!,
                                projectId: projectId,
                                decision: decision,
                                pipeline: fork.Mode,
                                renderResult: new JsonObject
                                {
                                    ["status"] = "failed",
                                    ["error"] = "No archived video segment was produced for this clip.",
                                });
                        }
                        catch (Exception exc)
                        {
                            // One bad decision must not skip the cleanup below.
                            Console.WriteLine(
                                "Recording unrendered clip candidate "
                                + $"{JsonUtil.Str(decision["chunk_index"])} failed: {exc.Message}");
                        }
                    }
                }
                fork.ParkedRenders.Clear();
                lock (segmentsGate)
                {
                    // A finished fork no longer holds any segment back.
                    fork.RetireBound = double.PositiveInfinity;
                }
            }

            void DropRemainingSegments()
            {
                // Delete leftover local segment copies (main thread, after every
                // fork's coordinator has drained). The map is only touched under
                // segmentsGate; the deletes happen outside the lock.
                List<string> leftovers;
                lock (segmentsGate)
                {
                    leftovers = segmentFiles.Values.ToList();
                    segmentFiles.Clear();
                }
                foreach (var path in leftovers)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (IOException exc)
                    {
                        Console.WriteLine($"Could not delete local segment {path}: {exc.Message}");
                    }
                }
            }

            (List<JsonObject>, string) ScoreChunk(
                Fork fork,
                JsonObject chunk,
                List<JsonObject> contextBefore,
                List<JsonObject> contextAfter,
                int visibleStart,
                int visibleEnd)
            {
                // Score one chunk via the LLM (runs on coordinator pool threads).
                Console.WriteLine(
                    $"Scoring chunk {JsonUtil.Str(chunk["chunk_index"])} for {fork.Mode}-form "
                    + $"clip candidates via {scoringBackend}");
                List<double>? sceneCuts = null;
                if (shotDetector is not null)
                {
                    var index = JsonUtil.Int(chunk["chunk_index"]);
                    lock (cutsGate)
                    {
                        sceneCuts = new[] { index - 1, index, index + 1 }
                            .SelectMany(neighbor =>
                                chunkCuts.TryGetValue(neighbor, out var cuts)
                                    ? cuts
                                    : new List<double>())
                            .Where(cut => visibleStart <= cut && cut <= visibleEnd)
                            .OrderBy(cut => cut)
                            .ToList();
                    }
                }
                return Llm.DetectClipCandidates(
                    transcript: JsonUtil.Str(chunk["transcript"]),
                    words: JsonUtil.Objects(chunk["words"]),
                    chunkIndex: JsonUtil.Int(chunk["chunk_index"]),
                    startSeconds: JsonUtil.Int(chunk["start_seconds"]),
                    endSeconds: JsonUtil.Int(chunk["end_seconds"]),
                    visibleStartSeconds: visibleStart,
                    visibleEndSeconds: visibleEnd,
                    contextWordsBefore: contextBefore,
                    contextWordsAfter: contextAfter,
                    reasoningEffort: llmReasoningEffort,
                    markerSeconds: llmMarkerSeconds,
                    pipelineMode: fork.Mode,
                    userInstructions: userInstructions,
                    targetLength: targetLength,
                    sourceContext: sourceContext,
                    researchContext: fork.ResearchContext,
                    audioContext: JsonUtil.Objects(chunk["audio_context"]),
                    sceneCuts: sceneCuts,
                    sourceMinutes: sourceMinutes);
            }

            if (llmEnabled)
            {
                foreach (var fork in forks)
                {
                    fork.Coordinator = new ClipScoringCoordinator(
                        scoreChunk: (chunk, before, after, visibleStart, visibleEnd) =>
                            ScoreChunk(fork, chunk, before, after, visibleStart, visibleEnd),
                        emitDecision: decision => EmitDecision(fork, decision),
                        chunkSeconds: chunkSeconds,
                        contextSeconds: llmContextSeconds,
                        concurrency: perForkConcurrency,
                        mergeGapSeconds: fork.MergeGapSeconds,
                        maxClipSeconds: fork.MaxClipSeconds,
                        onChunkComplete: (chunkIndex, minHeldStart) =>
                            RetireStaleSegments(fork, chunkIndex, minHeldStart),
                        onFinish: () => FinishLiveRenders(fork));
                }
            }
            coordinators = forks
                .Where(fork => fork.Coordinator is not null)
                .Select(fork => fork.Coordinator!)
                .ToList();

            void HandleVideoSegment(string segmentPath, int segmentIndex)
            {
                var probed = Render.ProbeStartTime(segmentPath);
                if (probed is null)
                {
                    Console.WriteLine(
                        $"Segment {segmentIndex} start probe failed; using nominal timing");
                }
                else
                {
                    lock (segmentsGate)
                    {
                        segmentStarts[segmentIndex] = probed.Value;
                    }
                }
                if (shotDetector is not null)
                {
                    try
                    {
                        var cuts = shotDetector.DetectCuts(
                            segmentPath, SegmentStart(segmentIndex));
                        lock (cutsGate)
                        {
                            chunkCuts[segmentIndex] = cuts;
                        }
                        Console.WriteLine(
                            $"Detected {cuts.Count} scene cut(s) in segment {segmentIndex}");
                    }
                    catch (Exception exc)
                    {
                        // Cuts are context, not a dependency; capture must survive.
                        Console.WriteLine(
                            $"Shot detection failed for segment {segmentIndex} (continuing): {exc.Message}");
                    }
                }
                if (storage is not null)
                {
                    var key = $"{archivePrefix}/{Path.GetFileName(segmentPath)}";
                    var uploaded = false;
                    for (var attempt = 1; attempt <= 3 && !uploaded; attempt++)
                    {
                        try
                        {
                            storage.UploadFile(segmentPath, key);
                            uploaded = true;
                        }
                        catch (Exception exc)
                        {
                            // The archive is best-effort; a failed segment upload
                            // must not end the capture.
                            Console.WriteLine(
                                $"Uploading video segment {segmentIndex} to "
                                + $"s3://{storage.Bucket}/{key} failed"
                                + (attempt < 3 ? $" (attempt {attempt}/3, retrying): " : " (giving up): ")
                                + exc.Message);
                        }
                    }
                    if (uploaded)
                    {
                        archivedSegments.Add(key);
                        Console.WriteLine(
                            $"Archived video segment {segmentIndex} to s3://{storage.Bucket}/{key}");
                    }
                }
                if (coordinators.Count > 0)
                {
                    // The local copy stays until no fork can reference it; each
                    // fork's emitter renders its clips from these files.
                    foreach (var fork in forks)
                    {
                        fork.Coordinator?.RunOnEmitter(
                            () => RegisterSegment(fork, segmentIndex, segmentPath));
                    }
                }
                else
                {
                    try
                    {
                        File.Delete(segmentPath);
                    }
                    catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
                    {
                        Console.WriteLine(
                            $"Could not delete local segment {segmentPath}: {exc.Message}");
                    }
                }
            }

            JsonObject FinalMetadata(params (string Key, JsonNode? Value)[] extra)
            {
                var metadata = new JsonObject
                {
                    ["platform"] = platform,
                    ["ingest"] = JsonUtil.CloneObj(ingestSettings),
                };
                foreach (var (key, value) in extra) metadata[key] = JsonUtil.C(value);
                // content_research mirrors the research file naming: the long/sole
                // fork's context, with the short fork's alongside in a combined run.
                var primaryResearch = (longFork ?? forks[0]).ResearchContext;
                if (primaryResearch is not null)
                    metadata["content_research"] = JsonUtil.CloneObj(primaryResearch);
                if (forks.Count > 1 && shortFork!.ResearchContext is not null)
                    metadata["content_research_short"] = JsonUtil.CloneObj(shortFork.ResearchContext);
                if (longformResult.Count > 0)
                    metadata["longform"] = JsonUtil.CloneObj(longformResult);
                if (storage is not null)
                {
                    metadata["source_archive"] = new JsonObject
                    {
                        ["bucket"] = storage.Bucket,
                        ["region"] = storage.Region,
                        ["prefix"] = archivePrefix,
                        ["container"] = "mpegts",
                        ["segment_seconds"] = chunkSeconds,
                        ["segments"] = archivedSegments.Count,
                        ["segment_keys"] = JsonUtil.Arr(
                            archivedSegments.Select(key => (JsonNode?)key)),
                    };
                }
                return metadata;
            }

            JsonObject MergedProjectMetadata(JsonObject metadata)
            {
                // Overlay this run's metadata onto the row's current metadata jsonb so
                // keys the backend wrote are not clobbered. Best-effort: falls back to the
                // run's metadata alone when the row cannot be re-read.
                JsonObject existing;
                try
                {
                    existing = db!.GetProject(projectId)["metadata"] as JsonObject
                        ?? new JsonObject();
                }
                catch (Exception exc)
                {
                    Console.WriteLine($"Could not re-read project metadata before writing: {exc.Message}");
                    existing = new JsonObject();
                }
                return JsonUtil.Merged(existing, metadata);
            }

            var editDescription = forks.Count > 1
                ? "short- and long-form edits"
                : $"a {pipelineMode}-form edit";
            Console.WriteLine(
                $"Capturing {sourceUrl} ({platform} {sourceType}, via {downloader}) in {chunkSeconds}s chunks"
                + (maxChunks > 0 ? $" ({maxChunks} chunk max)" : "")
                + $" for {editDescription}"
                + (llmEnabled
                    ? $"; scoring clips via {scoringBackend}"
                    : "; LLM scoring disabled")
                + (storage is not null
                    ? $"; archiving source to s3://{storage.Bucket}/{archivePrefix}"
                    : "")
                + ".");

            captureStarted = true;
            try
            {
                var chunkCount = Capture.CaptureAudioChunks(
                    sourceUrl: sourceUrl,
                    outputDir: audioDir,
                    chunkSeconds: chunkSeconds,
                    maxChunks: maxChunks,
                    streamlinkQuality: streamlinkQuality,
                    onChunk: (audioPath, chunkIndex, startSeconds, endSeconds) => ProcessChunk(
                        db: db,
                        records: records!,
                        projectId: projectId,
                        audioPath: audioPath,
                        chunkIndex: chunkIndex,
                        startSeconds: startSeconds,
                        endSeconds: endSeconds,
                        deepgramModel: deepgramModel,
                        coordinators: coordinators,
                        sceneCuts: LockedCutsFor(chunkCuts, cutsGate, chunkIndex),
                        wordSpans: wordSpans,
                        spansGate: spansGate,
                        chunkWords: chunkWords,
                        wordsGate: wordsGate),
                    downloader: downloader,
                    sourceType: sourceType,
                    archiveDir: archiveDir,
                    onVideoSegment: archiveDir is not null ? HandleVideoSegment : null,
                    shouldStop: StopRequestedPoll);
                // Drains in-flight scoring, stitches, renders, and cleans up; on a
                // cancel nothing new is dispatched but finished scores still land.
                // Each Finish() blocks until that fork's emitter exits, so the
                // shared segment copies can be dropped safely once every fork has
                // drained.
                Exception? finishError = null;
                foreach (var fork in forks)
                {
                    if (fork.Coordinator is null) continue;
                    try
                    {
                        fork.Coordinator.Finish(cancelled: stopRequested);
                    }
                    catch (Exception exc)
                    {
                        // Keep draining the other fork before surfacing this.
                        finishError = exc;
                        Console.WriteLine($"The {fork.Mode} fork failed while finishing: {exc.Message}");
                    }
                }
                DropRemainingSegments();
                if (finishError is not null) throw finishError;

                if (longFork is not null && !stopRequested)
                {
                    var longform = EditLongform(
                        db: db,
                        records: records!,
                        projectId: projectId,
                        projectDir: projectDir,
                        clipsBucket: clipsBucket,
                        renderedLog: longFork.RenderedLog,
                        chunkCount: chunkCount,
                        chunkSeconds: chunkSeconds,
                        targetLength: targetLength,
                        userInstructions: userInstructions,
                        researchContext: longFork.ResearchContext,
                        reasoningEffort: llmReasoningEffort,
                        chunkCuts: chunkCuts,
                        cutsGate: cutsGate,
                        wordSpans: wordSpans,
                        spansGate: spansGate,
                        thumbnailsEnabled: thumbnailsEnabled);
                    foreach (var pair in longform) longformResult[pair.Key] = JsonUtil.C(pair.Value);
                }

                var metadata = FinalMetadata(("chunks", chunkCount));
                bool cancelled;
                if (db is not null)
                {
                    var merged = MergedProjectMetadata(metadata);
                    if (stopRequested)
                    {
                        cancelled = true;
                    }
                    else
                    {
                        // Guarded: matches nothing when the backend flipped the row to
                        // 'stopping' after our last poll, in which case we acknowledge
                        // the cancel instead of writing 'ready'. A Supabase outage
                        // here must not fail an otherwise finished run.
                        try
                        {
                            cancelled = db.UpdateProjectStatusGuarded(
                                projectId,
                                status: "ready",
                                whenStatusIn: new[] { "ingesting" },
                                metadata: merged) is null;
                        }
                        catch (Exception writeExc)
                        {
                            Console.WriteLine(
                                $"Could not write final 'ready' status: {writeExc.Message}");
                            cancelled = false;
                        }
                    }
                    if (cancelled)
                    {
                        try
                        {
                            db.UpdateProjectStatusGuarded(
                                projectId,
                                status: "cancelled",
                                whenStatusIn: new[] { "stopping", "cancelled" },
                                metadata: merged);
                        }
                        catch (Exception writeExc)
                        {
                            Console.WriteLine(
                                $"Could not write final 'cancelled' status: {writeExc.Message}");
                        }
                    }
                }
                else
                {
                    cancelled = stopRequested;
                }

                var finalStatus = cancelled ? "cancelled" : "ready";
                records!.UpdateProject(("status", finalStatus), ("metadata", metadata));
                Console.WriteLine($"Project {projectId} {finalStatus} with {chunkCount} chunk(s).");
            }
            catch (Exception exc)
            {
                // Best-effort teardown; it must never mask the capture failure,
                // and every fork must drain even when another fork's teardown
                // fails.
                foreach (var fork in forks)
                {
                    if (fork.Coordinator is null) continue;
                    try
                    {
                        fork.Coordinator.Finish(cancelled: true);
                    }
                    catch
                    {
                    }
                }
                try
                {
                    DropRemainingSegments();
                }
                catch
                {
                }
                var metadata = FinalMetadata();
                records!.UpdateProject(
                    ("status", "failed"), ("error", exc.Message), ("metadata", metadata));
                if (db is not null)
                {
                    // A Supabase outage during these writes must not mask the
                    // real error being rethrown below.
                    var merged = MergedProjectMetadata(metadata);
                    JsonObject? failed = null;
                    var failedWriteLanded = false;
                    try
                    {
                        failed = db.UpdateProjectStatusGuarded(
                            projectId,
                            status: "failed",
                            whenStatusIn: new[] { "created", "ingesting" },
                            metadata: merged,
                            error: exc.Message);
                        failedWriteLanded = true;
                    }
                    catch (Exception writeExc)
                    {
                        Console.WriteLine(
                            $"Could not write final 'failed' status: {writeExc.Message}");
                    }
                    if (failedWriteLanded && failed is null)
                    {
                        // The row was already stopping/cancelled; finish as cancelled.
                        try
                        {
                            db.UpdateProjectStatusGuarded(
                                projectId,
                                status: "cancelled",
                                whenStatusIn: new[] { "stopping", "cancelled" },
                                metadata: merged);
                        }
                        catch (Exception writeExc)
                        {
                            Console.WriteLine(
                                $"Could not write final 'cancelled' status: {writeExc.Message}");
                        }
                    }
                }
                throw;
            }
        }
        catch (Exception exc) when (!captureStarted)
        {
            // Failures after capture starts are covered by the try/catch above;
            // this one only handles setup failures (the Python version re-raises
            // after best-effort status writes, and so does this).
            if (records is not null)
                records.UpdateProject(("status", "failed"), ("error", exc.Message));
            if (projectRow is not null)
            {
                var failed = db!.UpdateProjectStatusGuarded(
                    args.ProjectId!,
                    status: "failed",
                    whenStatusIn: new[] { "created", "ingesting" },
                    error: exc.Message);
                if (failed is null)
                {
                    // The row was already stopping/cancelled; finish as cancelled.
                    db.UpdateProjectStatusGuarded(
                        args.ProjectId!,
                        status: "cancelled",
                        whenStatusIn: new[] { "stopping", "cancelled" });
                }
            }
            throw;
        }
        finally
        {
            foreach (var registration in signalRegistrations) registration.Dispose();
            shotDetector?.Dispose();
        }
    }

    private static List<double>? LockedCutsFor(
        Dictionary<int, List<double>> chunkCuts, object gate, int chunkIndex)
    {
        lock (gate)
        {
            return chunkCuts.TryGetValue(chunkIndex, out var cuts) ? cuts.ToList() : null;
        }
    }

    /// <summary>Every detected scene cut in the chunks a window touches, plus one chunk
    /// of margin on each side so boundary snapping can look just past the edges.
    /// Callers hold the cuts lock.</summary>
    private static List<double> CutsNearWindow(
        Dictionary<int, List<double>> chunkCuts,
        int chunkSeconds,
        double startSeconds,
        double endSeconds)
    {
        var first = (int)(startSeconds / chunkSeconds) - 1;
        var last = (int)(endSeconds / chunkSeconds) + 1;
        var cuts = new List<double>();
        for (var index = first; index <= last; index++)
            if (chunkCuts.TryGetValue(index, out var segmentCuts))
                cuts.AddRange(segmentCuts);
        cuts.Sort();
        return cuts;
    }

    /// <summary>Best-effort source duration in minutes via yt-dlp; null when unknown.</summary>
    private static double? ProbeSourceMinutes(string sourceUrl)
    {
        try
        {
            var command = new List<string> { "yt-dlp", "--quiet", "--no-warnings" };
            command.AddRange(Cookies.YtdlpCookieArgs());
            command.AddRange(new[] { "--skip-download", "--print", "duration", sourceUrl });
            var (code, stdout, _) = Proc.Run(command, timeoutSeconds: 60);
            if (code != 0 || stdout.Trim().Length == 0) return null;
            var firstLine = stdout.Trim().Split('\n')[0];
            if (!double.TryParse(firstLine, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var durationSeconds))
                return null;
            return durationSeconds > 0 ? durationSeconds / 60 : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pass 2: one global editor call sees every rendered pass-1 candidate and
    /// makes the final cut — keep/drop, plus tightening within a segment's own
    /// window — before stitching. Any editor failure falls back to stitching all
    /// pass-1 selections, so this pass can only improve a run.</summary>
    private static JsonObject EditLongform(
        SupabaseClient? db,
        ProjectRecords records,
        string projectId,
        string projectDir,
        string clipsBucket,
        List<JsonObject> renderedLog,
        int chunkCount,
        int chunkSeconds,
        string? targetLength,
        string? userInstructions,
        JsonObject? researchContext,
        string reasoningEffort,
        Dictionary<int, List<double>> chunkCuts,
        object cutsGate,
        List<(double Start, double End)> wordSpans,
        object spansGate,
        bool thumbnailsEnabled = false)
    {
        var entries = renderedLog
            .OrderBy(clip => JsonUtil.Double(clip["start_seconds"]))
            .ToList();
        JsonObject? editorMeta = null;
        if (entries.Count >= 2)
        {
            try
            {
                var edit = Editor.SelectLongformSegments(
                    candidates: entries,
                    sourceMinutes: chunkCount * chunkSeconds / 60.0,
                    targetLength: targetLength,
                    researchContext: researchContext,
                    userInstructions: userInstructions,
                    reasoningEffort: reasoningEffort);
                if ((edit["selections"] as JsonArray)?.Count is null or 0)
                    throw new PipelineError("the editor kept no segments");
                var (finalEntries, appliedMeta) = ApplyLongformSelections(
                    entries: entries,
                    edit: edit,
                    projectDir: projectDir,
                    chunkCuts: chunkCuts,
                    cutsGate: cutsGate,
                    wordSpans: wordSpans,
                    spansGate: spansGate,
                    chunkSeconds: chunkSeconds);
                editorMeta = appliedMeta;
                var decisionRow = new JsonObject { ["type"] = "longform_edit" };
                foreach (var pair in editorMeta) decisionRow[pair.Key] = JsonUtil.C(pair.Value);
                decisionRow["selections"] = JsonUtil.C(edit["selections"]);
                records.AppendDecision(decisionRow);
                Console.WriteLine(
                    $"Long-form editor kept {JsonUtil.Str(editorMeta["kept_segments"])} of "
                    + $"{JsonUtil.Str(editorMeta["candidate_segments"])} candidate segment(s)"
                    + (JsonUtil.Truthy(editorMeta["trimmed_segments"])
                        ? $" ({JsonUtil.Str(editorMeta["trimmed_segments"])} tightened)"
                        : ""));
                entries = finalEntries;
            }
            catch (Exception exc)
            {
                Console.WriteLine(
                    $"Long-form editor pass failed; stitching all pass-1 selections: {exc.Message}");
                editorMeta = new JsonObject
                {
                    ["status"] = "failed",
                    ["error"] = exc.Message,
                };
                var decisionRow = new JsonObject { ["type"] = "longform_edit" };
                foreach (var pair in editorMeta) decisionRow[pair.Key] = JsonUtil.C(pair.Value);
                records.AppendDecision(decisionRow);
            }
        }

        return StitchLongform(
            db: db,
            records: records,
            projectId: projectId,
            projectDir: projectDir,
            clipsBucket: clipsBucket,
            entries: entries,
            editor: editorMeta,
            researchContext: researchContext,
            reasoningEffort: reasoningEffort,
            thumbnailsEnabled: thumbnailsEnabled);
    }

    /// <summary>Materialize the editor's selections: keep each chosen rendered segment,
    /// re-cutting any whose final window was tightened (snapped to scene cuts,
    /// never extended). Returns (final entries, editor metadata).</summary>
    private static (List<JsonObject> Entries, JsonObject EditorMeta) ApplyLongformSelections(
        List<JsonObject> entries,
        JsonObject edit,
        string projectDir,
        Dictionary<int, List<double>> chunkCuts,
        object cutsGate,
        List<(double Start, double End)> wordSpans,
        object spansGate,
        int chunkSeconds)
    {
        var segmentsDir = Path.Combine(projectDir, "longform", "segments");
        var finalEntries = new List<JsonObject>();
        var trimmed = 0;
        var selections = edit["selections"] as JsonArray ?? new JsonArray();
        for (var order = 0; order < selections.Count; order++)
        {
            if (selections[order] is not JsonObject selection) continue;
            var candidateIndex = JsonUtil.Int(selection["index"]);
            if (candidateIndex < 0 || candidateIndex >= entries.Count)
            {
                Console.WriteLine(
                    $"Skipping long-form selection {order}: candidate index {candidateIndex} "
                    + $"is out of range (have {entries.Count} candidates)");
                continue;
            }
            var candidate = entries[candidateIndex];
            var entry = JsonUtil.With(candidate, ("editor_reason", JsonUtil.C(selection["reason"])));
            var start = JsonUtil.Double(selection["start_seconds"]);
            var end = JsonUtil.Double(selection["end_seconds"]);
            var needsTrim =
                start - JsonUtil.Double(candidate["start_seconds"]) > 0.25
                || JsonUtil.Double(candidate["end_seconds"]) - end > 0.25;
            if (needsTrim)
            {
                List<double> nearbyCuts;
                lock (cutsGate)
                {
                    nearbyCuts = CutsNearWindow(chunkCuts, chunkSeconds, start, end);
                }
                JsonObject? snapInfo = null;
                if (nearbyCuts.Count > 0)
                {
                    List<(double Start, double End)> spans;
                    lock (spansGate)
                    {
                        spans = wordSpans.ToList();
                    }
                    (start, end, snapInfo) = Shots.SnapWindow(
                        start, end, cuts: nearbyCuts, wordSpans: spans);
                }
                start = Math.Max(start, JsonUtil.Double(candidate["start_seconds"]));
                end = Math.Min(end, JsonUtil.Double(candidate["end_seconds"]));
                if (end - start >= 2.0)
                {
                    var outputPath = Path.Combine(segmentsDir, $"segment_{order:000}.mp4");
                    Render.TrimClip(
                        sourcePath: JsonUtil.Str(candidate["path"]),
                        outputPath: outputPath,
                        startOffsetSeconds: start - JsonUtil.Double(candidate["start_seconds"]),
                        durationSeconds: end - start);
                    entry["path"] = outputPath;
                    entry["start_seconds"] = start;
                    entry["end_seconds"] = end;
                    if (snapInfo is not null) entry["boundary_snap"] = snapInfo;
                    trimmed++;
                }
            }

            finalEntries.Add(entry);
        }

        var editorMeta = new JsonObject
        {
            ["status"] = "applied",
            ["model"] = JsonUtil.C(edit["model"]),
            ["edit_notes"] = JsonUtil.C(edit["edit_notes"]),
            ["title"] = JsonUtil.C(edit["title"]),
            ["arithmetic"] = JsonUtil.C(edit["arithmetic"]),
            ["candidate_segments"] = entries.Count,
            ["kept_segments"] = finalEntries.Count,
            ["dropped_segments"] = entries.Count - finalEntries.Count,
            ["trimmed_segments"] = trimmed,
            ["candidates_dropped_from_prompt"] = JsonUtil.C(edit["candidates_dropped_from_prompt"]),
        };
        return (
            finalEntries.OrderBy(e => JsonUtil.Double(e["start_seconds"])).ToList(),
            editorMeta);
    }

    /// <summary>Concatenate the final segments chronologically into one long-form
    /// video, persist it as longform_edits version 1, and return its metadata (or
    /// a failure/empty record — stitching problems must not fail the whole run).</summary>
    private static JsonObject StitchLongform(
        SupabaseClient? db,
        ProjectRecords records,
        string projectId,
        string projectDir,
        string clipsBucket,
        List<JsonObject> entries,
        JsonObject? editor = null,
        JsonObject? researchContext = null,
        string reasoningEffort = Defaults.DEFAULT_LLM_REASONING_EFFORT,
        bool thumbnailsEnabled = false)
    {
        if (entries.Count == 0)
        {
            Console.WriteLine("No rendered segments to stitch into a long-form video.");
            return new JsonObject { ["status"] = "empty", ["segments"] = 0 };
        }

        var ordered = entries.OrderBy(clip => JsonUtil.Double(clip["start_seconds"])).ToList();
        var title = editor is not null && JsonUtil.Truthy(editor["title"])
            ? JsonUtil.Str(editor["title"])
            : JsonUtil.StrOrNull(ordered[0]["title"]);
        var outputPath = Path.Combine(projectDir, "longform", "longform.mp4");
        var totalSeconds = ordered.Sum(clip =>
            JsonUtil.Double(clip["end_seconds"]) - JsonUtil.Double(clip["start_seconds"]));
        Console.WriteLine(
            $"Stitching {ordered.Count} segment(s) (~{Py.F(totalSeconds / 60, 1)} min) "
            + $"into {outputPath}");

        try
        {
            Stitch.StitchClips(
                clipPaths: ordered.Select(clip => JsonUtil.Str(clip["path"])).ToList(),
                outputPath: outputPath);
        }
        catch (Exception exc)
        {
            Console.WriteLine($"Long-form stitch failed: {exc.Message}");
            return new JsonObject
            {
                ["status"] = "failed",
                ["error"] = exc.Message,
                ["segments"] = ordered.Count,
            };
        }

        var segmentWindows = new JsonArray();
        foreach (var clip in ordered)
        {
            segmentWindows.Add(new JsonObject
            {
                ["chunk_index"] = JsonUtil.C(clip["chunk_index"]),
                ["title"] = JsonUtil.C(clip["title"]),
                ["start_seconds"] = JsonUtil.C(clip["start_seconds"]),
                ["end_seconds"] = JsonUtil.C(clip["end_seconds"]),
            });
        }
        var result = new JsonObject
        {
            ["status"] = "rendered",
            ["version"] = 1,
            ["title"] = title,
            ["local_path"] = Path.GetRelativePath(Directory.GetCurrentDirectory(), outputPath),
            ["filename"] = Path.GetFileName(outputPath),
            ["segments"] = ordered.Count,
            ["duration_seconds"] = Py.Round(totalSeconds, 3),
            ["segment_windows"] = segmentWindows,
        };
        if (editor is not null) result["editor"] = JsonUtil.CloneObj(editor);

        string? thumbnailPath = null;
        try
        {
            thumbnailPath = Path.ChangeExtension(outputPath, ".jpg");
            Render.ExtractThumbnail(
                clipPath: outputPath, outputPath: thumbnailPath, atSeconds: totalSeconds / 2);
        }
        catch (Exception exc)
        {
            thumbnailPath = null;
            Console.WriteLine($"Long-form thumbnail extraction failed (non-fatal): {exc.Message}");
        }

        JsonObject? thumbnails = null;
        if (thumbnailsEnabled)
        {
            try
            {
                thumbnails = Thumbnails.GenerateThumbnails(
                    longformPath: outputPath,
                    entries: ordered,
                    title: title,
                    researchContext: researchContext,
                    outputDir: Path.Combine(projectDir, "thumbnails"),
                    reasoningEffort: reasoningEffort);
            }
            catch (Exception exc)
            {
                Console.WriteLine($"Thumbnail generation failed (non-fatal): {exc.Message}");
            }
        }
        if (thumbnails is not null) result["thumbnails"] = thumbnails;

        if (db is not null)
        {
            try
            {
                var storageKey = $"projects/{projectId}/longform/{Path.GetFileName(outputPath)}";
                var videoUrl = UploadVideoOrLocalUrl(
                    db, bucket: clipsBucket, key: storageKey, path: outputPath, label: "Long-form video");
                result["bucket"] = clipsBucket;
                result["storage_path"] = storageKey;
                result["video_url"] = videoUrl;
                if (thumbnailPath is not null)
                {
                    try
                    {
                        var thumbnailKey =
                            $"projects/{projectId}/longform/{Path.GetFileName(thumbnailPath)}";
                        result["thumbnail_url"] = db.UploadStorageObject(
                            bucket: clipsBucket, key: thumbnailKey, path: thumbnailPath);
                        result["thumbnail_storage_path"] = thumbnailKey;
                    }
                    catch (Exception exc)
                    {
                        Console.WriteLine($"Long-form thumbnail upload failed (non-fatal): {exc.Message}");
                    }
                }
                if (thumbnails is not null)
                {
                    foreach (var variantNode in thumbnails["variants"] as JsonArray ?? new JsonArray())
                    {
                        if (variantNode is not JsonObject variant) continue;
                        try
                        {
                            var variantPath = JsonUtil.Str(variant["local_path"]);
                            var variantKey =
                                $"projects/{projectId}/thumbnails/{Path.GetFileName(variantPath)}";
                            variant["url"] = db.UploadStorageObject(
                                bucket: clipsBucket, key: variantKey, path: variantPath);
                            variant["storage_path"] = variantKey;
                        }
                        catch (Exception exc)
                        {
                            Console.WriteLine($"Thumbnail variant upload failed (non-fatal): {exc.Message}");
                        }
                    }
                }
                db.InsertLongformEdit(
                    projectId: projectId,
                    version: 1,
                    status: "rendered",
                    videoUrl: videoUrl,
                    thumbnailUrl: DisplayThumbnailUrl(result),
                    durationSeconds: Py.Round(totalSeconds, 3),
                    segments: segmentWindows,
                    editor: editor,
                    metadata: new JsonObject
                    {
                        ["source"] = "longform_stitch",
                        ["render"] = JsonUtil.CloneObj(result),
                    });
            }
            catch (Exception exc)
            {
                Console.WriteLine($"Long-form upload failed (kept locally at {outputPath}): {exc.Message}");
                result["upload_error"] = exc.Message;
            }
        }

        records.AppendLongformEdit(new JsonObject
        {
            ["project_id"] = projectId,
            ["version"] = 1,
            ["status"] = "rendered",
            ["video_url"] = JsonUtil.Truthy(result["video_url"])
                ? JsonUtil.C(result["video_url"])
                : JsonUtil.C(result["local_path"]),
            ["thumbnail_url"] = DisplayThumbnailUrl(result),
            ["duration_seconds"] = Py.Round(totalSeconds, 3),
            ["segments"] = (JsonArray)segmentWindows.DeepClone(),
            ["editor"] = editor is null ? null : JsonUtil.CloneObj(editor),
            ["revision"] = null,
            ["metadata"] = new JsonObject
            {
                ["source"] = "longform_stitch",
                ["render"] = JsonUtil.CloneObj(result),
            },
        });
        Console.WriteLine($"Long-form video ready: {outputPath}");
        return result;
    }

    // Supabase storage rejects objects over the project's global size cap
    // (~50 MB here) with a 413; renders bigger than this go to the API's
    // local /media mirror instead of a doomed upload.
    private const long MAX_STORAGE_UPLOAD_BYTES = 48L * 1024 * 1024;

    /// <summary>URL for a render served from the API's /media mirror of
    /// outputs/ (HIGHLIGHTER_MEDIA_BASE overrides the local default).</summary>
    private static string LocalMediaUrl(string path)
    {
        var relative = Path
            .GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(path))
            .Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("outputs/")) relative = relative["outputs/".Length..];
        var mediaBase = Environment.GetEnvironmentVariable("HIGHLIGHTER_MEDIA_BASE");
        if (string.IsNullOrWhiteSpace(mediaBase)) mediaBase = "http://localhost:5199/media";
        return $"{mediaBase.TrimEnd('/')}/{relative}";
    }

    /// <summary>Upload a rendered video, falling back to its local /media URL
    /// when it exceeds the storage size cap or the upload fails. Never throws —
    /// a failed upload must not cost the row that references the render.</summary>
    private static string UploadVideoOrLocalUrl(
        SupabaseClient db, string bucket, string key, string path, string label)
    {
        if (new FileInfo(path).Length > MAX_STORAGE_UPLOAD_BYTES)
        {
            Console.WriteLine($"{label} exceeds the storage size cap; serving via local /media.");
            return LocalMediaUrl(path);
        }
        try
        {
            return db.UploadStorageObject(bucket: bucket, key: key, path: path);
        }
        catch (Exception exc)
        {
            Console.WriteLine($"{label} upload failed; serving via local /media: {exc.Message}");
            return LocalMediaUrl(path);
        }
    }

    /// <summary>The thumbnail a longform_edits row shows: the selected generated
    /// variant when one was uploaded, else the midpoint frame.</summary>
    private static string? DisplayThumbnailUrl(JsonObject result)
    {
        if (result["thumbnails"] is JsonObject thumbnails
            && thumbnails["variants"] is JsonArray variants
            && JsonUtil.TryDouble(thumbnails["selected_index"], out var selectedIndex)
            && (int)selectedIndex >= 0
            && (int)selectedIndex < variants.Count
            && variants[(int)selectedIndex] is JsonObject selected
            && JsonUtil.Truthy(selected["url"]))
        {
            return JsonUtil.Str(selected["url"]);
        }
        return JsonUtil.StrOrNull(result["thumbnail_url"]);
    }

    /// <summary>Classify a source URL. Returns (platform, source_type, project name).</summary>
    public static (string Platform, string SourceType, string Name) DetectSource(string url)
    {
        Uri parsed;
        try
        {
            parsed = new Uri(url);
        }
        catch (UriFormatException)
        {
            throw new PipelineError(
                $"Unsupported source URL (expected a twitch.tv or youtube URL): {url}");
        }
        var host = parsed.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        var path = parsed.AbsolutePath.TrimEnd('/');
        var segments = path.Split('/').Where(segment => segment.Length > 0).ToList();

        if (host.EndsWith("twitch.tv"))
        {
            if (segments.Count >= 2 && segments[^2] == "videos")
                return ("twitch", "video", $"twitch video {segments[^1]}");
            var channel = segments.Count > 0 ? segments[^1] : "twitch";
            return ("twitch", "livestream", $"{channel} livestream");
        }

        if (host.EndsWith("youtube.com"))
        {
            if (segments.Count > 0 && segments[^1] == "live" && segments.Count >= 2)
                return ("youtube", "livestream", $"{segments[^2].TrimStart('@')} livestream");
            var videoId = QueryParam(parsed.Query, "v");
            if (!string.IsNullOrEmpty(videoId))
                return ("youtube", "video", $"youtube video {videoId}");
            if (segments.Count > 0)
                return ("youtube", "livestream", $"{segments[^1].TrimStart('@')} livestream");
        }

        if (host == "youtu.be" && segments.Count > 0)
            return ("youtube", "video", $"youtube video {segments[^1]}");

        throw new PipelineError(
            $"Unsupported source URL (expected a twitch.tv or youtube URL): {url}");
    }

    private static string? QueryParam(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            if (Uri.UnescapeDataString(pair[..separator]) == name)
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }
        return null;
    }

    private static JsonObject StoreChunk(
        SupabaseClient? db,
        ProjectRecords records,
        string projectId,
        string audioPath,
        int chunkIndex,
        int startSeconds,
        int endSeconds,
        string deepgramModel,
        List<double>? sceneCuts = null)
    {
        Console.WriteLine($"Transcribing chunk {chunkIndex} ({startSeconds}s-{endSeconds}s)");
        var transcript = Transcribe.TranscribeAudioFile(audioPath, model: deepgramModel);
        var words = new JsonArray();
        foreach (var word in JsonUtil.Objects(transcript["words"]))
            words.Add(WithAbsoluteTimes(word, startSeconds));
        var transcriptMetadata = transcript["metadata"] as JsonObject ?? new JsonObject();
        var metadata = new JsonObject
        {
            ["audio_path"] = Path.GetRelativePath(Directory.GetCurrentDirectory(), audioPath),
            ["transcription_backend"] = JsonUtil.C(transcript["backend"]),
            ["transcription"] = JsonUtil.CloneObj(transcriptMetadata),
        };
        if (sceneCuts is not null)
            metadata["scene_cuts"] = JsonUtil.Arr(sceneCuts.Select(cut => (JsonNode?)cut));

        var transcriptText = JsonUtil.Str(transcript["transcript"]);
        if (db is not null)
        {
            db.InsertTranscriptChunk(
                projectId: projectId,
                chunkIndex: chunkIndex,
                startSeconds: startSeconds,
                endSeconds: endSeconds,
                transcript: transcriptText,
                words: words,
                metadata: metadata);
        }
        records.AppendChunk(new JsonObject
        {
            ["project_id"] = projectId,
            ["chunk_index"] = chunkIndex,
            ["start_seconds"] = startSeconds,
            ["end_seconds"] = endSeconds,
            ["transcript"] = transcriptText,
            ["words"] = (JsonArray)words.DeepClone(),
            ["metadata"] = JsonUtil.CloneObj(metadata),
        });

        Console.WriteLine(
            $"Stored chunk {chunkIndex}: {transcriptText[..Math.Min(120, transcriptText.Length)]}");
        return new JsonObject
        {
            ["transcript"] = transcriptText,
            ["words"] = (JsonArray)words.DeepClone(),
            ["metadata"] = JsonUtil.CloneObj(transcriptMetadata),
        };
    }

    private static void ProcessChunk(
        SupabaseClient? db,
        ProjectRecords records,
        string projectId,
        string audioPath,
        int chunkIndex,
        int startSeconds,
        int endSeconds,
        string deepgramModel,
        List<ClipScoringCoordinator> coordinators,
        List<double>? sceneCuts,
        List<(double Start, double End)> wordSpans,
        object spansGate,
        Dictionary<int, JsonArray>? chunkWords = null,
        object? wordsGate = null)
    {
        var stored = StoreChunk(
            db: db,
            records: records,
            projectId: projectId,
            audioPath: audioPath,
            chunkIndex: chunkIndex,
            startSeconds: startSeconds,
            endSeconds: endSeconds,
            deepgramModel: deepgramModel,
            sceneCuts: sceneCuts);
        lock (spansGate)
        {
            foreach (var word in JsonUtil.Objects(stored["words"]))
                wordSpans.Add((
                    JsonUtil.Double(word["absolute_start"]),
                    JsonUtil.Double(word["absolute_end"])));
        }
        if (chunkWords is not null && wordsGate is not null)
        {
            lock (wordsGate)
            {
                chunkWords[chunkIndex] = (JsonArray)stored["words"]!.DeepClone();
            }
        }
        // Scoring happens on each coordinator's pool: this chunk is dispatched
        // once the NEXT chunk's transcript arrives, so the model sees both
        // boundaries. The chunk object is read-only inside the coordinators.
        foreach (var coordinator in coordinators)
        {
            coordinator.AddChunk(new JsonObject
            {
                ["chunk_index"] = chunkIndex,
                ["start_seconds"] = startSeconds,
                ["end_seconds"] = endSeconds,
                ["transcript"] = JsonUtil.C(stored["transcript"]),
                ["words"] = JsonUtil.C(stored["words"]),
                ["audio_path"] = audioPath,
            });
        }
    }

    private static void RenderAndStoreClip(
        SupabaseClient? db,
        ProjectRecords records,
        string projectId,
        string projectDir,
        string clipsBucket,
        JsonObject decision,
        List<string> segmentPaths,
        double firstSegmentStartSeconds,
        List<JsonObject> renderedLog,
        string pipeline,
        string filenameSuffix = "",
        bool reframeEnabled = false,
        double reframeInterval = Defaults.DEFAULT_REFRAME_FRAME_INTERVAL_SECONDS,
        List<double>? sceneCuts = null,
        JsonObject? researchContext = null,
        bool captionsEnabled = false,
        JsonArray? clipWords = null)
    {
        var filename = Render.ClipFilename(
            chunkIndex: JsonUtil.Int(decision["chunk_index"]),
            startSeconds: JsonUtil.Double(decision["start_seconds"]),
            endSeconds: JsonUtil.Double(decision["end_seconds"]),
            suffix: filenameSuffix);
        var outputPath = Path.Combine(projectDir, "clips", filename);

        JsonObject renderResult;
        try
        {
            Render.RenderClipFromSegments(
                segmentPaths: segmentPaths,
                outputPath: outputPath,
                firstSegmentStartSeconds: firstSegmentStartSeconds,
                startSeconds: JsonUtil.Double(decision["start_seconds"]),
                endSeconds: JsonUtil.Double(decision["end_seconds"]));

            renderResult = new JsonObject
            {
                ["status"] = "rendered",
                ["local_path"] = Path.GetRelativePath(Directory.GetCurrentDirectory(), outputPath),
                ["filename"] = filename,
            };
            var thumbnailPath = ExtractClipThumbnail(outputPath, decision);
            string? verticalPath = null;
            if (reframeEnabled)
            {
                var reframed = ReframeClip(
                    clipPath: outputPath,
                    decision: decision,
                    sceneCuts: sceneCuts,
                    researchContext: researchContext,
                    frameIntervalSeconds: reframeInterval);
                if (reframed is not null)
                {
                    var (path, cropTrack) = reframed.Value;
                    verticalPath = path;
                    renderResult["vertical_path"] = Path.GetRelativePath(
                        Directory.GetCurrentDirectory(), verticalPath);
                    decision = JsonUtil.With(decision, ("reframe", cropTrack));
                }
            }
            string? captionedPath = null;
            if (verticalPath is not null && captionsEnabled && clipWords is { Count: > 0 })
            {
                captionedPath = Captions.CaptionClip(
                    verticalPath: verticalPath,
                    words: clipWords,
                    clipStartSeconds: JsonUtil.Double(decision["start_seconds"]),
                    clipEndSeconds: JsonUtil.Double(decision["end_seconds"]),
                    outputPath: Path.Combine(
                        Path.GetDirectoryName(verticalPath)!,
                        $"{Path.GetFileNameWithoutExtension(verticalPath)}_captions.mp4"));
                if (captionedPath is not null)
                {
                    renderResult["captioned_path"] = Path.GetRelativePath(
                        Directory.GetCurrentDirectory(), captionedPath);
                    Console.WriteLine(
                        $"Captioned clip {JsonUtil.Str(decision["chunk_index"])} to "
                        + Path.GetFileName(captionedPath));
                }
            }
            if (db is not null)
            {
                var storageKey = $"projects/{projectId}/clips/{filename}";
                var videoUrl = UploadVideoOrLocalUrl(
                    db, bucket: clipsBucket, key: storageKey, path: outputPath, label: "Clip");
                renderResult["bucket"] = clipsBucket;
                renderResult["storage_path"] = storageKey;
                renderResult["video_url"] = videoUrl;
                if (thumbnailPath is not null)
                {
                    try
                    {
                        var thumbnailKey =
                            $"projects/{projectId}/clips/{Path.GetFileName(thumbnailPath)}";
                        renderResult["thumbnail_url"] = db.UploadStorageObject(
                            bucket: clipsBucket, key: thumbnailKey, path: thumbnailPath);
                        renderResult["thumbnail_storage_path"] = thumbnailKey;
                    }
                    catch (Exception exc)
                    {
                        Console.WriteLine($"Thumbnail upload failed (non-fatal): {exc.Message}");
                    }
                }
                if (verticalPath is not null)
                {
                    var verticalKey =
                        $"projects/{projectId}/clips/{Path.GetFileName(verticalPath)}";
                    renderResult["vertical_url"] = UploadVideoOrLocalUrl(
                        db, bucket: clipsBucket, key: verticalKey, path: verticalPath,
                        label: "Vertical clip");
                    renderResult["vertical_storage_path"] = verticalKey;
                }
                if (captionedPath is not null)
                {
                    var captionedKey =
                        $"projects/{projectId}/clips/{Path.GetFileName(captionedPath)}";
                    renderResult["captioned_url"] = UploadVideoOrLocalUrl(
                        db, bucket: clipsBucket, key: captionedKey, path: captionedPath,
                        label: "Captioned clip");
                    renderResult["captioned_storage_path"] = captionedKey;
                }
            }

            renderedLog.Add(new JsonObject
            {
                ["path"] = outputPath,
                ["chunk_index"] = JsonUtil.C(decision["chunk_index"]),
                ["title"] = JsonUtil.C(decision["title"]),
                ["description"] = JsonUtil.C(decision["description"]),
                ["score"] = JsonUtil.C(decision["score"]),
                ["reason"] = JsonUtil.C(decision["reason"]),
                ["start_seconds"] = JsonUtil.C(decision["start_seconds"]),
                ["end_seconds"] = JsonUtil.C(decision["end_seconds"]),
            });
            Console.WriteLine($"Rendered clip {JsonUtil.Str(decision["chunk_index"])} to {outputPath}");
        }
        catch (Exception exc)
        {
            renderResult = new JsonObject { ["status"] = "failed", ["error"] = exc.Message };
            Console.WriteLine(
                $"Clip render failed for chunk {JsonUtil.Str(decision["chunk_index"])}: {exc.Message}");
        }

        StoreClipDecision(
            db: db,
            records: records,
            projectId: projectId,
            decision: decision,
            pipeline: pipeline,
            renderResult: renderResult);
    }

    /// <summary>Best-effort blur-pad vertical next to the rendered clip. Returns
    /// (verticalPath, cropTrack) or null — a reframe failure keeps the 16:9.</summary>
    private static (string VerticalPath, JsonObject CropTrack)? ReframeClip(
        string clipPath,
        JsonObject decision,
        List<double>? sceneCuts,
        JsonObject? researchContext,
        double frameIntervalSeconds = Defaults.DEFAULT_REFRAME_FRAME_INTERVAL_SECONDS)
    {
        try
        {
            var duration = JsonUtil.Double(decision["end_seconds"])
                - JsonUtil.Double(decision["start_seconds"]);
            var relativeCuts = (sceneCuts ?? new List<double>())
                .Where(cut => JsonUtil.Double(decision["start_seconds"]) < cut
                    && cut < JsonUtil.Double(decision["end_seconds"]))
                .Select(cut => Py.Round(cut - JsonUtil.Double(decision["start_seconds"]), 2))
                .ToList();
            var cropTrack = Reframe.PlanCropTrack(
                clipPath: clipPath,
                clipDurationSeconds: duration,
                sceneCuts: relativeCuts,
                title: JsonUtil.Str(decision["title"]),
                description: JsonUtil.Str(decision["description"]),
                researchContext: researchContext,
                frameIntervalSeconds: frameIntervalSeconds);
            var verticalPath = Path.Combine(
                Path.GetDirectoryName(clipPath)!,
                $"{Path.GetFileNameWithoutExtension(clipPath)}_vertical.mp4");
            Reframe.RenderVertical(
                sourcePath: clipPath,
                outputPath: verticalPath,
                keyframes: JsonUtil.Objects(cropTrack["keyframes"]));
            Console.WriteLine(
                $"Reframed clip {JsonUtil.Str(decision["chunk_index"])} to {Path.GetFileName(verticalPath)} "
                + $"({(cropTrack["keyframes"] as JsonArray)?.Count ?? 0} framing(s))");
            return (verticalPath, cropTrack);
        }
        catch (Exception exc)
        {
            Console.WriteLine(
                $"Auto-reframe failed for chunk {JsonUtil.Str(decision["chunk_index"])} "
                + $"(keeping the 16:9 clip): {exc.Message}");
            return null;
        }
    }

    /// <summary>Best-effort first-frame JPEG next to the rendered mp4; null on failure.</summary>
    private static string? ExtractClipThumbnail(string clipPath, JsonObject decision)
    {
        try
        {
            var thumbnailPath = Path.ChangeExtension(clipPath, ".jpg");
            Render.ExtractThumbnail(
                clipPath: clipPath, outputPath: thumbnailPath, atSeconds: 0.0);
            return thumbnailPath;
        }
        catch (Exception exc)
        {
            Console.WriteLine(
                $"Thumbnail extraction failed for {Path.GetFileName(clipPath)} (non-fatal): {exc.Message}");
            return null;
        }
    }

    private static void StoreClipDecision(
        SupabaseClient? db,
        ProjectRecords records,
        string projectId,
        JsonObject decision,
        string pipeline,
        JsonObject? renderResult = null)
    {
        // The fork that made the decision; in a combined run both forks' records
        // interleave, and revisions read only the long-form ones.
        decision = JsonUtil.With(decision, ("pipeline", pipeline));
        if (renderResult is not null)
            decision = JsonUtil.With(decision, ("render", JsonUtil.CloneObj(renderResult)));

        records.AppendDecision(JsonUtil.CloneObj(decision));

        if (!JsonUtil.Truthy(decision["is_clip_worthy"]))
        {
            var reason = JsonUtil.Str(decision["reason"]);
            Console.WriteLine(
                $"No clip candidate for chunk {JsonUtil.Str(decision["chunk_index"])}: "
                + reason[..Math.Min(120, reason.Length)]);
            return;
        }

        var clipStatus = "detected";
        string? videoUrl = null;
        string? verticalUrl = null;
        string? captionedUrl = null;
        if (renderResult is not null)
        {
            clipStatus = JsonUtil.StrOrNull(renderResult["status"]) ?? "detected";
            videoUrl = JsonUtil.StrOrNull(renderResult["video_url"])
                ?? JsonUtil.StrOrNull(renderResult["local_path"]);
            verticalUrl = JsonUtil.StrOrNull(renderResult["vertical_url"])
                ?? JsonUtil.StrOrNull(renderResult["vertical_path"]);
            captionedUrl = JsonUtil.StrOrNull(renderResult["captioned_url"])
                ?? JsonUtil.StrOrNull(renderResult["captioned_path"]);
        }

        var clipMetadata = new JsonObject
        {
            ["source"] = "llm",
            ["pipeline"] = pipeline,
            ["chunk_index"] = JsonUtil.C(decision["chunk_index"]),
            ["model"] = JsonUtil.C(decision["model"]),
            ["reasoning_effort"] = JsonUtil.C(decision["reasoning_effort"]),
            ["reason"] = JsonUtil.C(decision["reason"]),
            ["research_sources"] = JsonUtil.C(decision["research_sources"]) ?? new JsonArray(),
            ["raw_decision"] = JsonUtil.C(decision["raw_decision"]),
            ["boundary_snap"] = JsonUtil.C(decision["boundary_snap"]),
            ["reframe"] = JsonUtil.C(decision["reframe"]),
            ["render"] = renderResult is null ? null : JsonUtil.CloneObj(renderResult),
            ["thumbnail_url"] = JsonUtil.C(renderResult?["thumbnail_url"]),
            ["merged_from"] = JsonUtil.C(decision["merged_from"]),
        };
        records.AppendClip(new JsonObject
        {
            ["project_id"] = projectId,
            ["title"] = JsonUtil.C(decision["title"]),
            ["description"] = JsonUtil.C(decision["description"]),
            ["start_seconds"] = JsonUtil.C(decision["start_seconds"]),
            ["end_seconds"] = JsonUtil.C(decision["end_seconds"]),
            ["score"] = JsonUtil.C(decision["score"]),
            ["video_url"] = videoUrl,
            ["vertical_url"] = verticalUrl,
            ["captioned_url"] = captionedUrl,
            ["status"] = clipStatus,
            ["metadata"] = JsonUtil.CloneObj(clipMetadata),
        });
        if (db is not null)
        {
            db.InsertClip(
                projectId: projectId,
                title: JsonUtil.Str(decision["title"]),
                description: JsonUtil.StrOrNull(decision["description"]),
                startSeconds: JsonUtil.Double(decision["start_seconds"]),
                endSeconds: JsonUtil.Double(decision["end_seconds"]),
                score: decision["score"],
                videoUrl: renderResult is not null
                    ? JsonUtil.StrOrNull(renderResult["video_url"])
                    : null,
                verticalUrl: renderResult is not null
                    ? JsonUtil.StrOrNull(renderResult["vertical_url"])
                    : null,
                captionedUrl: renderResult is not null
                    ? JsonUtil.StrOrNull(renderResult["captioned_url"])
                    : null,
                status: clipStatus,
                metadata: clipMetadata);
        }

        Console.WriteLine(
            "Stored clip "
            + $"{JsonUtil.Str(decision["chunk_index"])}: {JsonUtil.Str(decision["title"])} "
            + $"({JsonUtil.Str(decision["start_seconds"])}s-{JsonUtil.Str(decision["end_seconds"])}s, "
            + $"score {JsonUtil.Str(decision["score"])})");
    }

    /// <summary>Archive segment indexes a clip window spans (end is exclusive
    /// when it lands exactly on a segment boundary). Measured starts sit at or
    /// after the nominal boundary, so a window can begin in the previous
    /// segment's tail or end before a nominal segment's content starts.</summary>
    public static (int First, int Last) WindowSegmentRange(
        double startSeconds,
        double endSeconds,
        int chunkSeconds,
        Func<int, double> segmentStart)
    {
        var first = (int)(startSeconds / chunkSeconds);
        var last = (int)(Math.Max(startSeconds, endSeconds - 0.001) / chunkSeconds);
        while (first > 0 && segmentStart(first) > startSeconds) first--;
        while (last > first && segmentStart(last) >= endSeconds) last--;
        return (first, last);
    }

    /// <summary>A segment's real position on the capture clock: its archived
    /// container timestamp relative to segment 0's. Copy cuts wait for a
    /// keyframe, so this sits at or after index * chunkSeconds — which is the
    /// fallback whenever a probe is missing.</summary>
    public static double MeasuredSegmentStart(
        Dictionary<int, double> segmentStarts, int index, int chunkSeconds)
    {
        if (!segmentStarts.TryGetValue(0, out var baseStart)
            || !segmentStarts.TryGetValue(index, out var start))
        {
            return index * (double)chunkSeconds;
        }
        return start - baseStart;
    }

    /// <summary>Words overlapping a clip window, pulled from the chunks that cover it.</summary>
    public static JsonArray WordsInWindow(
        Dictionary<int, JsonArray> chunkWords,
        int chunkSeconds,
        double startSeconds,
        double endSeconds)
    {
        var first = (int)(startSeconds / chunkSeconds);
        var last = (int)(Math.Max(startSeconds, endSeconds - 0.001) / chunkSeconds);
        var words = new JsonArray();
        for (var index = first; index <= last; index++)
        {
            if (!chunkWords.TryGetValue(index, out var chunk)) continue;
            foreach (var node in chunk)
            {
                if (node is not JsonObject word) continue;
                if (JsonUtil.Double(word["absolute_end"]) > startSeconds
                    && JsonUtil.Double(word["absolute_start"]) < endSeconds)
                {
                    words.Add(JsonUtil.CloneObj(word));
                }
            }
        }
        return words;
    }

    private static JsonObject WithAbsoluteTimes(JsonObject word, int chunkStartSeconds)
    {
        var output = JsonUtil.CloneObj(word);
        output["absolute_start"] = Py.Round(
            JsonUtil.DoubleOr(word.TryGetPropertyValue("start", out var start) ? start : null, 0)
            + chunkStartSeconds, 3);
        output["absolute_end"] = Py.Round(
            JsonUtil.DoubleOr(word.TryGetPropertyValue("end", out var end) ? end : null, 0)
            + chunkStartSeconds, 3);
        return output;
    }

    public static bool TruthyEnv(string key) =>
        (Config.Env(key) ?? "").ToLowerInvariant() is "1" or "true" or "yes";
}
