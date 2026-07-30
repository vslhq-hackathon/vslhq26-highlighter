namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/defaults.py — same names, same values.</summary>
public static class Defaults
{
    public const string DEFAULT_STREAM_URL = "https://www.twitch.tv/jasontheween";
    public const int DEFAULT_CHUNK_SECONDS = 90;
    public const int DEFAULT_MAX_CHUNKS = 1;
    public const string DEFAULT_STREAMLINK_QUALITY = "best";
    public const string DEFAULT_DEEPGRAM_MODEL = "nova-3";
    public const string DEFAULT_OPENROUTER_MODEL = "google/gemini-3.1-pro-preview";
    public const string DEFAULT_LLM_REASONING_EFFORT = "high";
    public const int DEFAULT_LLM_MARKER_SECONDS = 10;
    public const int DEFAULT_LLM_CONCURRENCY = 8;
    public const int DEFAULT_LLM_CONTEXT_SECONDS = 10;
    // Azure Speech calls in flight (pure API latency, no local CPU): 2 keeps a
    // healthy overlap without tripping the resource's rate limit.
    public const int DEFAULT_TRANSCRIBE_CONCURRENCY = 2;
    public const double DEFAULT_CLIP_MERGE_GAP_SECONDS = 1.0;
    public const double DEFAULT_MAX_CLIP_SECONDS = 120;
    // Editing-master handles: extra footage rendered on each side of a short
    // clip so the editor can extend past the LLM's cut points. Must stay well
    // under CHUNK_SECONDS — segment retirement keeps exactly one segment of
    // slack behind each render.
    public const double DEFAULT_CLIP_HANDLE_SECONDS = 5.0;
    // Auto-reframe frame sampling: one frame every this many seconds, plus one
    // just after each scene cut (0 disables the periodic frames).
    public const double DEFAULT_REFRAME_FRAME_INTERVAL_SECONDS = 5.0;
    // Shot-boundary detection (TransNetV2) and cut-aligned boundary snapping.
    public const int DEFAULT_SHOT_FPS = 25;
    public const double DEFAULT_SHOT_SNAP_TOLERANCE_SECONDS = 1.5;
    // Long-form mode selects bigger segments but keeps the merge gap tight: the
    // editor deliberately splits a passage around coughs, false starts, and dead
    // air, and a wide gap would glue those micro-cuts (junk included) back in.
    public const double DEFAULT_LONGFORM_MERGE_GAP_SECONDS = 1.0;
    public const double DEFAULT_LONGFORM_MAX_CLIP_SECONDS = 900;
    public const string DEFAULT_TARGET_LENGTH_MINUTES = "7-15";
    public const string DEFAULT_RESEARCH_MODEL = "anthropic/claude-sonnet-5";
    public const string DEFAULT_THUMBNAIL_MODEL = "google/gemini-3.1-flash-image";
    // Must be a template this machine's pycaps actually ships ("instagram" is
    // not one of them; pycaps aborts the captioned render if it's missing).
    public const string DEFAULT_CAPTION_TEMPLATE = "hype";
    // Model routing: OpenRouter runs first in every chat-model chain (Gemini
    // 3.1 Pro for editing/scoring, Sonnet 5 for research, the flash-image model
    // for thumbnails) with the Azure OpenAI deployments as the fallback (see
    // Providers.cs). Azure Speech transcription still runs first with Deepgram
    // as the fallback. Reasoning-capable Azure deployments run at the highest
    // effort their family accepts (AZURE_REASONING_EFFORT overrides).
    public const string DEFAULT_AZURE_SPEECH_LOCALE = "en-US";
    public const string DEFAULT_OUTPUT_ROOT = "outputs";
    // Empty by default: livestream source archiving to S3 is opt-in via S3_BUCKET.
    public const string DEFAULT_S3_BUCKET = "";
    public const string DEFAULT_AWS_REGION = "us-east-1";
    public const string DEFAULT_SUPABASE_CLIPS_BUCKET = "clips";
}
