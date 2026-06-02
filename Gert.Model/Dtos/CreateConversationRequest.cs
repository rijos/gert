namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>POST /api/projects/{pid}/conversations</c> (rest-api.md
/// § conversations). Unset fields inherit the project/user defaults.
/// </summary>
public sealed record CreateConversationRequest
{
    public string? Title { get; init; }

    public string? ModelId { get; init; }

    public ToolToggles? Tools { get; init; }

    public GenerationParams? Params { get; init; }
}
