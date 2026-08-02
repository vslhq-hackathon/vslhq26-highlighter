using System.Text;
using Highlighter.Web.Models;

namespace Highlighter.Web.Services;

/// <summary>IStudioBackend over the real Highlighter API. Tool results are
/// plain strings for the studio agent (and the thumbnail UI) — they report
/// what actually happened, including job ids the agent can poll. Render jobs
/// are watched in the background so the open project refreshes itself when
/// they finish.</summary>
public class ApiStudioBackend(StudioState state, ApiClient api) : IStudioBackend
{
    public async Task<string> GetProjectStatusAsync()
    {
        if (state.ProjectId is not { } id) return "No project is open.";
        var detail = await api.GetProjectAsync(id);
        var p = detail.Project;
        var text = new StringBuilder(
            $"Project \"{p.Name}\" ({Fmt.Kind(p.SourceType)}, {Fmt.Duration(p.DurationSeconds)}): "
            + $"status {p.Status}. Outputs: {Fmt.Outputs(p.Pipeline)} — "
            + $"{detail.Clips.Count} clip(s), {detail.LongformEdits.Count} long-form version(s).");
        if (p.Error is not null) text.Append($" Error: {p.Error}");
        if (detail.LongformEdits.FirstOrDefault() is { } latest)
            text.Append($" Long-form title: \"{latest.Title ?? p.Name}\" (v{latest.Version}).");
        return text.ToString();
    }

    public async Task<string> ListClipsAsync()
    {
        if (state.ProjectId is not { } id) return "No project is open.";
        var detail = await api.GetProjectAsync(id);
        if (detail.Clips.Count == 0) return "No clips yet.";
        return string.Join("\n", detail.Clips.Select(clip =>
            $"- [{clip.Id}] \"{clip.Title}\" ({Fmt.Duration(clip.DurationSeconds)}, "
            + $"score {Fmt.Score100(clip.Score)}, {clip.Status}"
            + $"{(clip.CaptionedUrl is not null ? ", captioned" : ", no captions")})"));
    }

    public async Task<string> GetLongformVersionsAsync()
    {
        if (state.ProjectId is not { } id) return "No project is open.";
        var detail = await api.GetProjectAsync(id);
        if (detail.LongformEdits.Count == 0) return "No long-form versions yet.";
        var lines = detail.LongformEdits.Select(edit =>
            $"v{edit.Version}: \"{edit.Title ?? detail.Project.Name}\" "
            + $"({Fmt.Duration(edit.DurationSeconds)}, {edit.Status}"
            + $"{(edit.RevisionRequest is { } req ? $", revision: \"{req}\"" : "")})"
            + (edit.Thumbnails is { Count: > 0 } thumbs
                ? $" — thumbnails: {string.Join(", ", thumbs.Select(t => $"#{t.Index} {t.Direction}"))}"
                  + $"; selected #{edit.SelectedThumbnail}"
                : ""));
        return string.Join("\n", lines);
    }

    public async Task<string> RerunResearchAsync(string focus)
    {
        if (state.ProjectId is not { } id) return "No project is open.";
        try
        {
            var job = await api.PostJobAsync($"/api/projects/{id}/research",
                new { mode = state.AgentContext == "long" ? "long" : "short", focus });
            WatchJob(job.Id, refreshOnExit: false);
            return $"Research rerun started (job {job.Id}). Check it with get_job_status.";
        }
        catch (ApiException exception)
        {
            return $"Research rerun failed to start: {exception.UserMessage}";
        }
    }

    public async Task<string> GenerateThumbnailsAsync(string? prompt)
    {
        if (state.ProjectId is not { } id) return "No project is open.";
        try
        {
            state.SetThumbJob(true);
            var job = await api.PostJobAsync($"/api/projects/{id}/thumbnails", new { prompt });
            WatchJob(job.Id, refreshOnExit: true, thumbJob: true);
            return $"Thumbnail generation started (job {job.Id}); new concepts land in the "
                + "thumbnail strip when it finishes (about a minute).";
        }
        catch (ApiException exception)
        {
            state.SetThumbJob(false, exception.UserMessage);
            return $"Thumbnail generation failed to start: {exception.UserMessage}";
        }
    }

    public async Task<string> SelectThumbnailAsync(int index)
    {
        if (state.ProjectId is not { } id) return "No project is open.";
        try
        {
            // A straight metadata write server-side — no worker, so this works
            // without a local run mirror.
            await api.PostAsync<LongformEditDto>(
                $"/api/projects/{id}/thumbnails/select", new { index });
            await state.RefreshDetailAsync();
            return $"Thumbnail #{index} is now the video's thumbnail.";
        }
        catch (ApiException exception)
        {
            return $"Selecting thumbnail #{index} failed: {exception.UserMessage}";
        }
    }

    public async Task<string> ImportThumbnailAsync(string fileName, byte[] content)
    {
        if (state.ProjectId is not { } id) return "No project is open.";
        try
        {
            await api.PostAsync<LongformEditDto>($"/api/projects/{id}/thumbnails/import",
                new { fileName, contentBase64 = Convert.ToBase64String(content) });
            await state.RefreshDetailAsync();
            return $"Imported {fileName} and selected it as the thumbnail.";
        }
        catch (ApiException exception)
        {
            return $"Import failed: {exception.UserMessage}";
        }
    }

    public async Task<string> ReviseLongformAsync(string request)
    {
        if (state.ProjectId is not { } id) return "No project is open.";
        try
        {
            var fromChat = state.AgentContext == "long";
            var job = await api.ReviseAsync(id, request, fromChat);
            // The persisted assistant reply carries this job id, so the server
            // can pair its completion row with the message that started it.
            if (fromChat) state.PendingAgentJobId = job.Id;
            WatchJob(job.Id, refreshOnExit: true, chatJob: fromChat);
            return $"Revision started (job {job.Id}). It renders the next long-form version — "
                + "usually a few minutes; check with get_job_status.";
        }
        catch (ApiException exception)
        {
            return $"Revision failed to start: {exception.UserMessage}";
        }
    }

    public async Task<string> ReformatClipAsync(string format, bool captions)
    {
        if (state.ProjectId is not { } id) return "No project is open.";
        if (state.ActiveClip is not { } clip) return "No clip is open in the editor.";
        try
        {
            var job = await api.PostJobAsync(
                $"/api/projects/{id}/clips/{clip.Id}/reformat", new { format, captions });
            WatchJob(job.Id, refreshOnExit: true);
            return $"Reformat started (job {job.Id}): {format} render of \"{clip.Title}\""
                + $"{(captions ? " with burned captions" : "")}.";
        }
        catch (ApiException exception)
        {
            return $"Reformat failed to start: {exception.UserMessage}";
        }
    }

    public async Task<string> GetJobStatusAsync(string jobId)
    {
        try
        {
            var job = await api.GetJobAsync(jobId.Trim());
            var line = $"{job.Id} ({job.Kind}): {job.State}";
            if (job.FailureReason is { } why) line += $" — {why}";
            if (job.State is "failed" or "killed")
            {
                var tail = await api.GetJobLogsAsync(job.Id, tail: 5);
                if (tail.Count > 0)
                    line += "\nLast log lines:\n"
                        + string.Join("\n", tail.Select(entry => $"  {entry.Line}"));
            }
            return line;
        }
        catch (ApiException exception)
        {
            return $"No job {jobId}: {exception.UserMessage}";
        }
    }

    public void WatchChatJob(string jobId) => WatchJob(jobId, refreshOnExit: true, chatJob: true);

    /// <summary>Background watcher: when a render job ends, refresh the open
    /// project so new media/thumbnails appear without a manual reload — and for
    /// chat-started jobs, re-pull the transcript so the server-written
    /// completion message appears in the open chat.</summary>
    private void WatchJob(string jobId, bool refreshOnExit, bool thumbJob = false, bool chatJob = false)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 240; i++) // up to ~20 minutes
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    var job = await api.GetJobAsync(jobId);
                    if (job.State is "starting" or "running") continue;
                    if (thumbJob)
                        state.SetThumbJob(false,
                            job.State == "succeeded" ? null : $"Generation {job.State}");
                    if (refreshOnExit) await state.RefreshDetailAsync();
                    if (chatJob) await RefreshChatAsync(jobId);
                    return;
                }
                if (thumbJob) state.SetThumbJob(false, "Generation timed out");
            }
            catch (Exception)
            {
                if (thumbJob) state.SetThumbJob(false);
            }
        });
    }

    /// <summary>The supervisor writes the completion row on job exit; give it a
    /// few beats to land, then swap the transcript in.</summary>
    private async Task RefreshChatAsync(string jobId)
    {
        if (state.ProjectId is not { } id) return;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var rows = await api.GetAgentMessagesAsync(id);
                if (rows.Any(row => row.JobId == jobId && row.JobFinal))
                {
                    state.ReplaceAgentMessages(rows.Select(row =>
                        new AgentChatMessage(row.Role, row.Text, row.JobId, row.JobFinal)));
                    return;
                }
            }
            catch (ApiException)
            {
                // Token expired or circuit signing out — stop quietly.
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
