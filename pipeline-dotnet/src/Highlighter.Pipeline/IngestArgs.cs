namespace Highlighter.Pipeline;

/// <summary>Port of ingest.py's _parse_args(): same flags, same env fallbacks,
/// same defaults. Parse() must run after Config.LoadEnv(), exactly as Python
/// parses after load_env().</summary>
public class IngestArgs
{
    public string? SourceUrl;
    public string Pipeline = "short";
    public string? Instructions;
    public string TargetMinutes = Defaults.DEFAULT_TARGET_LENGTH_MINUTES;
    public string? ProjectId;
    public string SourceType = "auto";
    public double? MinClipScore;
    public bool LocalOnly;
    public string? OutputRoot;
    public bool NoArchive;
    public bool NoResearch;
    public int ChunkSeconds = Defaults.DEFAULT_CHUNK_SECONDS;
    public int MaxChunks = Defaults.DEFAULT_MAX_CHUNKS;
    public string StreamlinkQuality = Defaults.DEFAULT_STREAMLINK_QUALITY;
    public string DeepgramModel = Defaults.DEFAULT_DEEPGRAM_MODEL;
    public bool NoLlm;
    public bool NoShots;
    public bool NoReframe;
    public double ReframeInterval = Defaults.DEFAULT_REFRAME_FRAME_INTERVAL_SECONDS;
    public bool NoCaptions;
    public bool NoThumbnails;
    public string LlmReasoningEffort = Defaults.DEFAULT_LLM_REASONING_EFFORT;
    public int LlmMarkerSeconds = Defaults.DEFAULT_LLM_MARKER_SECONDS;
    public int LlmConcurrency = Defaults.DEFAULT_LLM_CONCURRENCY;
    public int LlmContextSeconds = Defaults.DEFAULT_LLM_CONTEXT_SECONDS;
    public int TranscribeConcurrency = Defaults.DEFAULT_TRANSCRIBE_CONCURRENCY;

    public static IngestArgs Parse(string[] argv)
    {
        var args = new IngestArgs
        {
            // argparse defaults, resolved from the environment at parse time.
            Pipeline = Config.EnvOrNull("PIPELINE") ?? "short",
            Instructions = Config.EnvOrNull("USER_INSTRUCTIONS"),
            TargetMinutes = Config.Env("TARGET_MINUTES", Defaults.DEFAULT_TARGET_LENGTH_MINUTES),
            ProjectId = Config.EnvOrNull("PROJECT_ID"),
            SourceType = Config.EnvOrNull("SOURCE_TYPE") ?? "auto",
            MinClipScore = Config.FloatEnv("MIN_CLIP_SCORE"),
            ChunkSeconds = Config.IntEnv("CHUNK_SECONDS", Defaults.DEFAULT_CHUNK_SECONDS),
            MaxChunks = Config.IntEnv("MAX_CHUNKS", Defaults.DEFAULT_MAX_CHUNKS),
            StreamlinkQuality = Config.Env("STREAMLINK_QUALITY", Defaults.DEFAULT_STREAMLINK_QUALITY),
            DeepgramModel = Config.Env("DEEPGRAM_MODEL", Defaults.DEFAULT_DEEPGRAM_MODEL),
            ReframeInterval = Config.FloatEnv("REFRAME_INTERVAL")
                ?? Defaults.DEFAULT_REFRAME_FRAME_INTERVAL_SECONDS,
            LlmReasoningEffort = Config.Env(
                "OPENROUTER_REASONING_EFFORT", Defaults.DEFAULT_LLM_REASONING_EFFORT),
            LlmMarkerSeconds = Config.IntEnv(
                "LLM_MARKER_SECONDS", Defaults.DEFAULT_LLM_MARKER_SECONDS),
            LlmConcurrency = Config.IntEnv("LLM_CONCURRENCY", Defaults.DEFAULT_LLM_CONCURRENCY),
            LlmContextSeconds = Config.IntEnv(
                "LLM_CONTEXT_SECONDS", Defaults.DEFAULT_LLM_CONTEXT_SECONDS),
            TranscribeConcurrency = Config.IntEnv(
                "TRANSCRIBE_CONCURRENCY", Defaults.DEFAULT_TRANSCRIBE_CONCURRENCY),
        };

        var positionals = new List<string>();
        for (var i = 0; i < argv.Length; i++)
        {
            var (flag, inlineValue) = Argv.SplitFlag(argv[i]);
            string Next() => inlineValue ?? Argv.NextValue(argv, ref i, flag);
            switch (flag)
            {
                case "--pipeline":
                    args.Pipeline = Argv.Choice(Next(), "--pipeline", "short", "long", "both");
                    break;
                case "--instructions":
                    args.Instructions = Next();
                    break;
                case "--target-minutes":
                    args.TargetMinutes = Next();
                    break;
                case "--project-id":
                    args.ProjectId = Next();
                    break;
                case "--source-type":
                    args.SourceType = Argv.Choice(
                        Next(), "--source-type", "auto", "livestream", "video");
                    break;
                case "--min-clip-score":
                    args.MinClipScore = Argv.Float(Next(), "--min-clip-score");
                    break;
                case "--local-only":
                    args.LocalOnly = true;
                    break;
                case "--output-root":
                    args.OutputRoot = Next();
                    break;
                case "--no-archive":
                    args.NoArchive = true;
                    break;
                case "--no-research":
                    args.NoResearch = true;
                    break;
                case "--chunk-seconds":
                    args.ChunkSeconds = Argv.Int(Next(), "--chunk-seconds");
                    break;
                case "--max-chunks":
                    args.MaxChunks = Argv.Int(Next(), "--max-chunks");
                    break;
                case "--streamlink-quality":
                    args.StreamlinkQuality = Next();
                    break;
                case "--deepgram-model":
                    args.DeepgramModel = Next();
                    break;
                case "--no-llm":
                    args.NoLlm = true;
                    break;
                case "--no-shots":
                    args.NoShots = true;
                    break;
                case "--no-reframe":
                    args.NoReframe = true;
                    break;
                case "--reframe-interval":
                    args.ReframeInterval = Argv.Float(Next(), "--reframe-interval");
                    break;
                case "--no-captions":
                    args.NoCaptions = true;
                    break;
                case "--no-thumbnails":
                    args.NoThumbnails = true;
                    break;
                case "--llm-reasoning-effort":
                    args.LlmReasoningEffort = Next();
                    break;
                case "--llm-marker-seconds":
                    args.LlmMarkerSeconds = Argv.Int(Next(), "--llm-marker-seconds");
                    break;
                case "--llm-concurrency":
                    args.LlmConcurrency = Argv.Int(Next(), "--llm-concurrency");
                    break;
                case "--llm-context-seconds":
                    args.LlmContextSeconds = Argv.Int(Next(), "--llm-context-seconds");
                    break;
                case "--transcribe-concurrency":
                    args.TranscribeConcurrency = Argv.Int(Next(), "--transcribe-concurrency");
                    break;
                default:
                    Argv.Positional(flag, positionals);
                    break;
            }
        }

        if (positionals.Count > 1)
            throw new PipelineError($"unrecognized arguments: {string.Join(" ", positionals.Skip(1))}");
        args.SourceUrl = positionals.FirstOrDefault();
        return args;
    }
}

/// <summary>The few argparse behaviors the CLIs rely on.</summary>
public static class Argv
{
    public static (string Flag, string? InlineValue) SplitFlag(string arg)
    {
        if (arg.StartsWith("--") && arg.Contains('='))
        {
            var separator = arg.IndexOf('=');
            return (arg[..separator], arg[(separator + 1)..]);
        }
        return (arg, null);
    }

    public static string NextValue(string[] argv, ref int i, string flag)
    {
        if (i + 1 >= argv.Length)
            throw new PipelineError($"argument {flag}: expected one argument");
        return argv[++i];
    }

    public static void Positional(string arg, List<string> positionals)
    {
        if (arg.StartsWith("--"))
            throw new PipelineError($"unrecognized arguments: {arg}");
        positionals.Add(arg);
    }

    public static string Choice(string value, string flag, params string[] choices)
    {
        if (!choices.Contains(value))
            throw new PipelineError(
                $"argument {flag}: invalid choice: '{value}' "
                + $"(choose from {string.Join(", ", choices.Select(c => $"'{c}'"))})");
        return value;
    }

    public static int Int(string value, string flag)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new PipelineError($"argument {flag}: invalid int value: '{value}'");
        return parsed;
    }

    public static double Float(string value, string flag)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new PipelineError($"argument {flag}: invalid float value: '{value}'");
        return parsed;
    }
}
