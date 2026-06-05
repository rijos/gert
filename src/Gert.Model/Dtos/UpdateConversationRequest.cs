namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>PATCH /api/projects/{pid}/conversations/{id}</c> (rest-api.md
/// § conversations): rename / switch model / toggle tools / archive. All
/// fields optional — only the supplied subset is updated.
/// </summary>
public sealed record UpdateConversationRequest
{
    public string? Title { get; init; }

    public string? ModelId { get; init; }

    public ToolToggles? Tools { get; init; }

    public GenerationParams? Params { get; init; }

    public bool? Archived { get; init; }
}
