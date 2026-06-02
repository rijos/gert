namespace Gert.Model.Dtos;

/// <summary>
/// Body of <c>POST /api/projects/{pid}/memory</c> (rest-api.md § memory):
/// <c>{ title, content, pinned? }</c> — add/edit an entry; it is (re)embedded
/// into the project's <c>rag.db</c> as <c>kind='memory'</c>.
/// </summary>
public sealed record CreateMemoryRequest
{
    public required string Title { get; init; }

    public required string Content { get; init; }

    /// <summary>Pinned entries are always injected, not just retrieved.</summary>
    public bool? Pinned { get; init; }
}
