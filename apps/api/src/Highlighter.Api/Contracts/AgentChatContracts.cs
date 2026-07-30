namespace Highlighter.Api.Contracts;

/// <summary>One studio-agent chat message. JobId marks a message that started a
/// pipeline job; JobFinal marks the server-written completion message for it.</summary>
public record AgentMessageDto(
    Guid Id,
    string Role,
    string Text,
    string? JobId,
    bool JobFinal,
    DateTimeOffset CreatedAt);

public record AgentMessageCreateDto(string? Role, string? Text, string? JobId = null);
