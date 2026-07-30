using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Highlighter.Pipeline;

namespace Highlighter.Cli;

/// <summary>Queue-worker mode for Container Apps Jobs: drain messages from the
/// pipeline-jobs storage queue, running each one's verb as a child process of
/// this same binary. The pipeline_jobs row is the durable state record; stdout/
/// stderr are teed to the shared outputs/api/jobs log file in the exact format
/// the API's log endpoints parse. Always exits 0 — retries are the queue's job,
/// not the platform's.</summary>
public static class RunQueued
{
    private static readonly TimeSpan Visibility = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SupervisePeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(15);
    // How long to keep polling an empty queue before the execution ends. KEDA
    // spawns executions from the message count, so normally a message is
    // already waiting; the window just absorbs enqueue/visibility jitter.
    private static readonly TimeSpan DrainPatience = TimeSpan.FromSeconds(45);

    public static async Task<int> Main()
    {
        Config.LoadEnv();
        var queue = new QueueClient(
            Config.RequiredEnv("JOBS_QUEUE_CONNECTION"),
            Config.Env("JOBS_QUEUE_NAME", "pipeline-jobs"),
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        var db = new JobsTable();

        var idleSince = DateTimeOffset.UtcNow;
        while (true)
        {
            QueueMessage? message = (await queue.ReceiveMessagesAsync(1, Visibility)).Value.FirstOrDefault();
            if (message is null)
            {
                if (DateTimeOffset.UtcNow - idleSince > DrainPatience) break;
                await Task.Delay(3000);
                continue;
            }
            try
            {
                await ProcessAsync(queue, db, message);
            }
            catch (Exception exception)
            {
                // Leave the message invisible; it reappears for a retry and the
                // dequeue-count guard below turns a second crash into 'failed'.
                Console.Error.WriteLine($"run-queued: message processing failed: {exception}");
            }
            idleSince = DateTimeOffset.UtcNow;
        }
        Console.WriteLine("run-queued: queue drained, exiting");
        return 0;
    }

    private static async Task ProcessAsync(QueueClient queue, JobsTable db, QueueMessage message)
    {
        var payload = JsonNode.Parse(message.Body.ToString()) as JsonObject
            ?? throw new InvalidOperationException("queue message is not a JSON object");
        var jobId = payload["job_id"]!.GetValue<string>();
        Console.WriteLine($"run-queued: picked up {jobId} (dequeue #{message.DequeueCount})");

        var row = db.Get(jobId);
        if (row is null)
        {
            Console.Error.WriteLine($"run-queued: no pipeline_jobs row for {jobId}; dropping message");
            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt);
            return;
        }
        var status = row["status"]?.GetValue<string>();
        var projectId = row["project_id"]?.GetValue<string>();
        var kind = row["kind"]?.GetValue<string>() ?? "ingest";

        if (status is "cancel_requested" or "killed")
        {
            db.Patch(jobId, new JsonObject { ["status"] = "killed", ["ended_at"] = Now() });
            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt);
            return;
        }
        if (status is not "pending")
        {
            // A previous worker claimed it and died (the message came back after
            // its visibility lapsed). Don't re-run half-done media work.
            Console.Error.WriteLine($"run-queued: {jobId} is '{status}' on redelivery; marking failed");
            db.Patch(jobId, new JsonObject
            {
                ["status"] = "failed",
                ["error"] = $"worker died mid-run (message redelivered, dequeue #{message.DequeueCount})",
                ["ended_at"] = Now(),
            });
            if (kind == "ingest" && projectId is not null)
                ReconcileProjectRow(projectId, exitCode: -1, jobId);
            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt);
            return;
        }

        db.Patch(jobId, new JsonObject
        {
            ["status"] = "running",
            ["started_at"] = Now(),
            ["worker"] = Environment.MachineName,
        }, guardStatus: "pending");

        var argv = (row["argv"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToList();
        var logName = row["log_name"]?.GetValue<string>() ?? $"queued-{kind}-{jobId}.log";
        var logDir = Path.Combine(Config.Env("OUTPUT_ROOT", "outputs"), "api", "jobs");
        Directory.CreateDirectory(logDir);
        using var sink = new StreamWriter(new FileStream(
            Path.Combine(logDir, logName), FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        void Log(string stream, string line)
        {
            sink.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{stream}] {line}");
            Console.WriteLine($"[{stream}] {line}");
        }
        Log("api", $"job {jobId} kind={kind}" + (projectId is null ? "" : $" project={projectId}"));
        Log("api", $"command: {Environment.ProcessPath} {string.Join(' ', argv)}");
        Log("api", $"worker: {Environment.MachineName}");

        var info = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in argv) info.ArgumentList.Add(argument);

        using var child = new Process { StartInfo = info };
        child.Start();
        var pumpOut = Task.Run(async () =>
        {
            while (await child.StandardOutput.ReadLineAsync() is { } line) Log("out", line);
        });
        var pumpErr = Task.Run(async () =>
        {
            while (await child.StandardError.ReadLineAsync() is { } line) Log("err", line);
        });

        var killRequested = await SuperviseAsync(queue, db, message, jobId, child, Log);
        await Task.WhenAll(pumpOut, pumpErr);
        await child.WaitForExitAsync();
        var exitCode = child.ExitCode;

        if (kind == "ingest" && projectId is not null && (exitCode != 0 || killRequested))
            ReconcileProjectRow(projectId, exitCode, jobId);

        var final = killRequested ? "killed" : exitCode == 0 ? "succeeded" : "failed";
        Log("api", $"exited — state {final}, exit code {exitCode}");
        db.Patch(jobId, new JsonObject
        {
            ["status"] = final,
            ["exit_code"] = exitCode,
            ["error"] = final == "failed" ? $"worker exited with code {exitCode}" : null,
            ["ended_at"] = Now(),
        });
        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt);
    }

    /// <summary>Waits for the child while renewing the message's invisibility and
    /// polling the row for a force-cancel. Returns true when a kill was delivered.</summary>
    private static async Task<bool> SuperviseAsync(QueueClient queue, JobsTable db,
        QueueMessage message, string jobId, Process child, Action<string, string> log)
    {
        var popReceipt = message.PopReceipt;
        var lastRenew = DateTimeOffset.UtcNow;
        while (!child.HasExited)
        {
            await Task.Delay(SupervisePeriod);
            if (child.HasExited) break;

            if (DateTimeOffset.UtcNow - lastRenew > Visibility / 3)
            {
                try
                {
                    popReceipt = (await queue.UpdateMessageAsync(
                        message.MessageId, popReceipt, visibilityTimeout: Visibility)).Value.PopReceipt;
                    lastRenew = DateTimeOffset.UtcNow;
                }
                catch (Exception exception)
                {
                    log("api", $"visibility renewal failed: {exception.Message}");
                }
            }

            string? status;
            try
            {
                status = db.Get(jobId)?["status"]?.GetValue<string>();
            }
            catch
            {
                continue; // transient DB blip: keep supervising
            }
            if (status == "cancel_requested")
            {
                log("api", "force-cancel requested: sending SIGTERM");
                TrySignal(child.Id, "-TERM");
                using var grace = new CancellationTokenSource(KillGrace);
                try
                {
                    await child.WaitForExitAsync(grace.Token);
                }
                catch (OperationCanceledException)
                {
                    log("api", $"still alive after {KillGrace.TotalSeconds:0}s, sending SIGKILL");
                    try
                    {
                        child.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
                return true;
            }
        }
        // The API's message stays fresh so a crash right here still redelivers.
        _ = popReceipt;
        return false;
    }

    /// <summary>Same decision table as the API's supervisor: only after the worker
    /// process is OBSERVED dead may a terminal status be written over a
    /// non-terminal one, guarded so a concurrent writer wins harmlessly.</summary>
    private static void ReconcileProjectRow(string projectId, int exitCode, string jobId)
    {
        try
        {
            var supabase = new SupabaseClient();
            var (guard, status, error) = supabase.GetProjectStatus(projectId) switch
            {
                "created" => ("created", "failed",
                    $"worker exited before claiming the project (exit {exitCode}); see job {jobId} logs"),
                "ingesting" => ("ingesting", "failed",
                    $"worker terminated unexpectedly (exit {exitCode}); see job {jobId} logs"),
                "stopping" => ("stopping", "cancelled", (string?)null),
                _ => (null, "", null),
            };
            if (guard is null) return;
            supabase.UpdateProjectStatusGuarded(projectId, status, [guard], error: error);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"run-queued: project row reconciliation failed: {exception.Message}");
        }
    }

    private static void TrySignal(int pid, string signal)
    {
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("/bin/kill")
            {
                ArgumentList = { signal, pid.ToString(CultureInfo.InvariantCulture) },
                UseShellExecute = false,
            });
            kill?.WaitForExit();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"run-queued: {signal} delivery failed: {exception.Message}");
        }
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff+00:00",
        CultureInfo.InvariantCulture);
}

/// <summary>Minimal PostgREST access to pipeline_jobs with the same env contract
/// as SupabaseClient (whose helpers are all table-specific).</summary>
internal sealed class JobsTable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _baseUrl;
    private readonly string _key;

    public JobsTable()
    {
        var supabase = new SupabaseClient();
        _baseUrl = supabase.BaseUrl;
        _key = supabase.Key;
    }

    public JsonObject? Get(string jobId)
    {
        var rows = Send(HttpMethod.Get, $"pipeline_jobs?id=eq.{jobId}", null);
        return rows is JsonArray { Count: > 0 } array ? array[0] as JsonObject : null;
    }

    public void Patch(string jobId, JsonObject body, string? guardStatus = null)
    {
        var query = $"pipeline_jobs?id=eq.{jobId}"
            + (guardStatus is null ? "" : $"&status=eq.{guardStatus}");
        Send(HttpMethod.Patch, query, body);
    }

    private JsonArray? Send(HttpMethod method, string pathAndQuery, JsonObject? body)
    {
        using var request = new HttpRequestMessage(method, $"{_baseUrl}/rest/v1/{pathAndQuery}");
        request.Headers.Add("apikey", _key);
        request.Headers.Add("Authorization", $"Bearer {_key}");
        request.Headers.Add("Prefer", "return=representation");
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = Http.Send(request);
        var text = response.Content.ReadAsStringAsync().Result;
        if (!response.IsSuccessStatusCode)
            throw new PipelineError($"pipeline_jobs {method} failed ({(int)response.StatusCode}): {text}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonArray;
    }
}
