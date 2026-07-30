using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Highlighter.Web.Models;

namespace Highlighter.Web.Services;

/// <summary>Typed, authenticated client for the Highlighter API. Requests are
/// built by factory lambdas (HttpRequestMessage is single-use) so a 401 can be
/// retried once after a token refresh.</summary>
public sealed class ApiClient(IHttpClientFactory factory, AuthSession auth)
{
    // ---- projects ----

    public Task<List<ProjectSummaryDto>> ListProjectsAsync(CancellationToken ct = default) =>
        GetAsync<List<ProjectSummaryDto>>("/api/projects", ct);

    public Task<ProjectDetailDto> GetProjectAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<ProjectDetailDto>($"/api/projects/{id}", ct);

    public Task<ProjectSummaryDto> CreateProjectAsync(CreateProjectRequest request, CancellationToken ct = default) =>
        SendAsync<ProjectSummaryDto>(() => JsonRequest(HttpMethod.Post, "/api/projects", request), ct);

    public Task<CancelResultDto> CancelProjectAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<CancelResultDto>(() => new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{id}/cancel"), ct);

    public async Task DeleteProjectAsync(Guid id, CancellationToken ct = default) =>
        (await SendRawAsync(() => new HttpRequestMessage(
            HttpMethod.Delete, $"/api/projects/{id}?force=true"), false, ct)).Dispose();

    public async Task DeleteClipAsync(Guid projectId, Guid clipId, CancellationToken ct = default) =>
        (await SendRawAsync(() => new HttpRequestMessage(
            HttpMethod.Delete, $"/api/projects/{projectId}/clips/{clipId}"), false, ct)).Dispose();

    public Task<List<TranscriptChunkDto>> GetTranscriptAsync(Guid id, bool words, CancellationToken ct = default) =>
        GetAsync<List<TranscriptChunkDto>>($"/api/projects/{id}/transcript?includeWords={words}", ct);

    // ---- actions ----

    public Task<JobDto> ReviseAsync(
        Guid id, string request, bool fromChat = false, CancellationToken ct = default) =>
        SendAsync<JobDto>(() => JsonRequest(HttpMethod.Post, $"/api/projects/{id}/revise",
            new ReviseRequestDto(request, fromChat)), ct);

    // ---- agent chat ----

    public Task<List<AgentMessageDto>> GetAgentMessagesAsync(
        Guid id, string context = "long", CancellationToken ct = default) =>
        GetAsync<List<AgentMessageDto>>($"/api/projects/{id}/agent/messages?context={context}", ct);

    public Task<AgentMessageDto> PostAgentMessageAsync(
        Guid id, string role, string text, string? jobId = null, string context = "long",
        CancellationToken ct = default) =>
        SendAsync<AgentMessageDto>(() => JsonRequest(HttpMethod.Post,
            $"/api/projects/{id}/agent/messages?context={context}",
            new { role, text, jobId }), ct);

    public Task<JobDto> PublishAsync(Guid id, PublishRequestDto request, CancellationToken ct = default) =>
        SendAsync<JobDto>(() => JsonRequest(HttpMethod.Post, $"/api/projects/{id}/publish", request), ct);

    // ---- editor ----

    public Task<EditorDocResponse> GetEditorDocAsync(
        Guid projectId, Guid? clipId, CancellationToken ct = default) =>
        GetAsync<EditorDocResponse>(EditorPath(projectId, clipId), ct);

    public Task<EditorDocResponse> SaveEditorDocAsync(
        Guid projectId, Guid? clipId, EditorDoc doc, CancellationToken ct = default) =>
        SendAsync<EditorDocResponse>(() => JsonRequest(HttpMethod.Put,
            EditorPath(projectId, clipId), new SaveEditorRequest(doc)), ct);

    public Task<JobDto> ExportEditAsync(
        Guid projectId, Guid? clipId, EditorDoc doc, CancellationToken ct = default) =>
        SendAsync<JobDto>(() => JsonRequest(HttpMethod.Post,
            EditorPath(projectId, clipId) + "/export", new SaveEditorRequest(doc)), ct);

    private static string EditorPath(Guid projectId, Guid? clipId) => clipId is { } clip
        ? $"/api/projects/{projectId}/clips/{clip}/editor"
        : $"/api/projects/{projectId}/longform/editor";

    /// <summary>POST an action endpoint that answers with a JobDto (202/200).</summary>
    public Task<JobDto> PostJobAsync<TBody>(string path, TBody body, CancellationToken ct = default) =>
        SendAsync<JobDto>(() => JsonRequest(HttpMethod.Post, path, body), ct);

    public Task<T> PostAsync<T>(string path, object body, CancellationToken ct = default) =>
        SendAsync<T>(() => JsonRequest(HttpMethod.Post, path, body), ct);

    // ---- jobs ----

    public Task<JobDto> GetJobAsync(string jobId, CancellationToken ct = default) =>
        GetAsync<JobDto>($"/api/jobs/{jobId}", ct);

    public Task<List<JobLogLineDto>> GetJobLogsAsync(string jobId, int tail = 100, CancellationToken ct = default) =>
        GetAsync<List<JobLogLineDto>>($"/api/jobs/{jobId}/logs?tail={tail}", ct);

    // ---- core ----

    private Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Get, path), ct);

    private static HttpRequestMessage JsonRequest<TBody>(HttpMethod method, string path, TBody body) =>
        new(method, path) { Content = JsonContent.Create(body) };

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        using var response = await SendRawAsync(make, retried: false, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        Func<HttpRequestMessage> make, bool retried, CancellationToken ct)
    {
        var request = make();
        if (await auth.GetValidAccessTokenAsync(ct) is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await factory.CreateClient(AuthApiClient.HttpClientName).SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && !retried
            && await auth.TryRefreshAsync(ct))
        {
            response.Dispose();
            return await SendRawAsync(make, retried: true, ct);
        }
        if (!response.IsSuccessStatusCode)
        {
            var failure = await ApiException.FromResponseAsync(response, ct);
            response.Dispose();
            throw failure;
        }
        return response;
    }
}
