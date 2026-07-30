using Highlighter.Web.Models;

namespace Highlighter.Web.Services;

/// <summary>The single seam between the studio UI (and its agent's tools) and
/// project data. Each method maps 1:1 to an apps/api endpoint; today's
/// implementation runs on the sample data, and the API hookup later is one
/// class implementing this interface over HTTP — nothing else changes.</summary>
public interface IStudioBackend
{
    /// <summary>GET /api/projects/{id}</summary>
    Task<string> GetProjectStatusAsync();

    /// <summary>GET /api/projects/{id}/clips</summary>
    Task<string> ListClipsAsync();

    /// <summary>GET /api/projects/{id}/longform</summary>
    Task<string> GetLongformVersionsAsync();

    /// <summary>POST /api/projects/{id}/research {mode, focus} → job</summary>
    Task<string> RerunResearchAsync(string focus);

    /// <summary>POST /api/projects/{id}/thumbnails {prompt} → job</summary>
    Task<string> GenerateThumbnailsAsync(string? prompt);

    /// <summary>PUT /api/projects/{id}/longform/{version}/thumbnail {index}</summary>
    Task<string> SelectThumbnailAsync(int index);

    /// <summary>POST /api/projects/{id}/thumbnails/import (base64 payload)</summary>
    Task<string> ImportThumbnailAsync(string fileName, byte[] content);

    /// <summary>POST /api/projects/{id}/revise {request} → job (long form only)</summary>
    Task<string> ReviseLongformAsync(string request);

    /// <summary>POST /api/projects/{id}/clips/{clipId}/reformat {format, captions} → job</summary>
    Task<string> ReformatClipAsync(string format, bool captions);

    /// <summary>GET /api/jobs/{id}</summary>
    Task<string> GetJobStatusAsync(string jobId);

    /// <summary>Re-arm the background watcher for a chat-started job (after a
    /// reload) so its completion message lands in the open chat.</summary>
    void WatchChatJob(string jobId);
}
