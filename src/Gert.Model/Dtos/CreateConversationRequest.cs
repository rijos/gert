namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>POST /api/projects/{pid}/conversations</c> (rest-api.md
/// section conversations). Unset fields inherit the project/user defaults.
/// </summary>
public sealed record CreateConversationRequest
{
    public string? Title { get; init; }

    /// <summary>Provider id (the <c>Gert:Chat:Providers</c> slug); unset uses the default provider.</summary>
    public string? ModelId { get; init; }

    public ToolToggles? Tools { get; init; }
}
