namespace Gert.Model.Rag;

/// <summary>
/// A per-project memory entry — the <c>GET /api/projects/{pid}/memory</c> list
/// shape (rest-api.md § memory). Stored as markdown under
/// <c>projects/{pid}/memory/</c> and embedded into the project's <c>rag.db</c>
/// as a <see cref="Document"/> with <see cref="DocumentKind.Memory"/>
/// (configuration.md § 2.3).
/// </summary>
public sealed record MemoryEntry
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Markdown body; null in list projections that omit it.</summary>
    public string? Content { get; init; }

    /// <summary>Pinned entries are always injected, not just retrieved.</summary>
    public bool Pinned { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
