using Highlighter.Cli;
using Highlighter.Pipeline;

const string usage =
    """
    usage: highlighter <command> [args]

    commands:
      ingest    Capture a Twitch/YouTube stream or video, transcribe chunks, and produce clips.
      revise    Revise a finished long-form edit with a natural-language request.
      publish   Publish a finished clip or long-form edit to social platforms.
      thumbnails  Regenerate or select long-form thumbnails (optionally with a prompt).
      research  Rerun content research for a finished project.
      reformat  Render a square center-crop copy of a clip (optionally captioned).
      reclip    Render a clip from a project's archived source video in S3.
      cleanup   Delete media objects referenced by pending media_cleanup_jobs rows.
      run-queued  Drain pipeline jobs from the Azure Storage queue (container worker mode).
      db-smoke  Insert a smoke-test project into Supabase.
    """;

var command = args.Length > 0 ? args[0] : null;
var rest = args.Skip(1).ToArray();

try
{
    switch (command)
    {
        case "ingest":
            Ingest.Main(rest);
            break;
        case "revise":
        {
            var positionals = new List<string>();
            string? outputRoot = null;
            for (var i = 0; i < rest.Length; i++)
            {
                var (flag, inlineValue) = Argv.SplitFlag(rest[i]);
                if (flag == "--output-root")
                    outputRoot = inlineValue ?? Argv.NextValue(rest, ref i, flag);
                else
                    Argv.Positional(flag, positionals);
            }
            if (positionals.Count != 2)
                throw new PipelineError(
                    "the following arguments are required: project_id, request");
            Revise.Main(positionals[0], positionals[1], outputRoot);
            break;
        }
        case "publish":
            Publish.Main(rest);
            break;
        case "thumbnails":
            Verbs.ThumbnailsMain(rest);
            break;
        case "research":
            Verbs.ResearchMain(rest);
            break;
        case "reformat":
            Verbs.ReformatMain(rest);
            break;
        case "reclip":
            Reclip.Main(rest);
            break;
        case "cleanup":
            Cleanup.Main(rest);
            break;
        case "run-queued":
            Environment.Exit(RunQueued.Main().GetAwaiter().GetResult());
            break;
        case "db-smoke":
            DbSmoke.Main();
            break;
        case "-h" or "--help" or "help":
            Console.WriteLine(usage);
            break;
        default:
            Console.Error.WriteLine(usage);
            Environment.Exit(2);
            break;
    }
}
catch (Exception exc)
{
    // Python prints the traceback and exits 1; mirror the exit code and put the
    // failure on stderr.
    Console.Error.WriteLine(exc.ToString());
    Environment.Exit(1);
}
