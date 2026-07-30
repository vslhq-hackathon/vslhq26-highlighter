using System.Text.Json.Nodes;
using Highlighter.Api.Contracts;
using Highlighter.Api.Infrastructure;
using Highlighter.Api.Services;

namespace Highlighter.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness only — /healthz gates on Supabase reachability, which would
        // make an orchestrator restart a healthy process during an upstream
        // blip. Container probes point here; /healthz stays the deep check.
        app.MapGet("/livez", () => Results.Ok(new { status = "alive" }))
            .WithName("GetLiveness");

        app.MapGet("/healthz",
            async (RepoLayout layout, SupabaseDb db, PipelineJobService jobs, CancellationToken ct) =>
            {
                var env = new HealthEnvDto(
                    SupabaseUrl: Has("SUPABASE_URL"),
                    SupabaseServiceRoleKey: Has("SUPABASE_SERVICE_ROLE_KEY") || Has("SUPABASE_SECRET_KEY"),
                    DeepgramApiKey: Has("DEEPGRAM_API_KEY"),
                    AzureSpeech: Has("AZURE_SPEECH_KEY")
                        && (Has("AZURE_SPEECH_ENDPOINT") || Has("AZURE_SPEECH_REGION")),
                    AzureOpenAI: Has("AZURE_OPENAI_API_KEY") && Has("AZURE_OPENAI_ENDPOINT"),
                    OpenRouterApiKey: Has("OPENROUTER_API_KEY"),
                    UploadPostApiKey: Has("UPLOAD_POST_API_KEY"),
                    UploadPostUser: Has("UPLOAD_POST_USER"));

                var command = layout.ResolveWorkerCommand();
                var worker = new HealthWorkerDto(command is not null, command?.Display);
                var binaries = new HealthBinariesDto(
                    Ffmpeg: RepoLayout.FindOnPath("ffmpeg") is not null,
                    YtDlp: RepoLayout.FindOnPath("yt-dlp") is not null,
                    Streamlink: RepoLayout.FindOnPath("streamlink") is not null);

                bool? reachable = db.IsConfigured ? await db.ProbeAsync(ct) : null;
                var supabase = new HealthSupabaseDto(db.IsConfigured, reachable);
                var outputs = new HealthOutputsDto(
                    layout.OutputsRoot, IsWritable(layout.OutputsRoot), layout.JobLogRoot);

                HealthCleanupDto? cleanup = null;
                int? orphans = null;
                if (reachable == true)
                {
                    try
                    {
                        cleanup = new HealthCleanupDto(
                            Pending: await db.CountCleanupJobsAsync("pending", ct),
                            Failed: await db.CountCleanupJobsAsync("failed", ct));
                        // Non-terminal rows with no job in this API instance's registry:
                        // either a worker owned by a previous API process (still fine —
                        // cooperative cancel works via the DB) or a dead run needing a
                        // cancel to reap it.
                        var nonTerminal = await db.ListNonTerminalProjectsAsync(ct);
                        orphans = nonTerminal.OfType<JsonObject>().Count(row =>
                            Guid.TryParse(row["id"]?.GetValue<string>(), out var projectId)
                            && jobs.ActiveForProject(projectId) is null);
                    }
                    catch (PostgrestException)
                    {
                        // Counts are informational; a failure here should not flip health.
                    }
                }

                // streamlink is optional (twitch livestreams only) and Azure/Deepgram/
                // OpenRouter degrade inside the worker, so none of those gate "ok".
                var ok = supabase.Configured && reachable == true
                    && worker.Resolved && binaries.Ffmpeg && binaries.YtDlp;
                var dto = new HealthDto(ok ? "ok" : "degraded",
                    env, worker, binaries, supabase, outputs, cleanup, orphans);
                return Results.Json(dto, statusCode: ok
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("GetHealth");
    }

    private static bool Has(string key) =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key));

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
