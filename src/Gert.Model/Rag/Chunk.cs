namespace Gert.Model.Rag;

/// <summary>
/// A retrievable text chunk of a <see cref="Document"/> - mirrors the
/// <c>chunks</c> row in a project's <c>rag.db</c> (storage-and-data.md
/// section rag.db). The integer <see cref="Id"/> is shared with the <c>vec_chunks</c>
/// and <c>fts_chunks</c> rowids so the three tables join cheaply.
/// </summary>
public sealed record Chunk
{
    public required long Id { get; init; }

    public required string DocumentId { get; init; }

    public required int Ordinal { get; init; }

    public required string Content { get; init; }

    /// <summary>Locator within the source - "p.4", "section 3".</summary>
    public string? Page { get; init; }

    public int? TokenCount { get; init; }
}
