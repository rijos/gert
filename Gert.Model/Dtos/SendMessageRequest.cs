namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>POST /api/projects/{pid}/conversations/{id}/messages</c>
/// (rest-api.md § sending a message). Unset <see cref="ModelId"/> /
/// <see cref="Tools"/> inherit the conversation defaults.
/// </summary>
public sealed record SendMessageRequest
{
    public required string Content { get; init; }

    public string? ModelId { get; init; }

    public ToolToggles? Tools { get; init; }
}
