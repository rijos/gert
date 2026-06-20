namespace Gert.Tools;

/// <summary>
/// One ranked RAG hit: the source document id + decoded title, its kind, the page locator, the
/// fused score, and the passage content - everything a tool needs to build a citation and a result
/// row. A contracts-level shape (the host maps its internal retrieved-chunk model onto it).
/// </summary>
public sealed record RagSearchHit
{
    /// <summary>The source document's id (for citation provenance).</summary>
    public required string DocId { get; init; }

    /// <summary>The human-readable source title (already decoded for display).</summary>
    public required string Title { get; init; }

    /// <summary>The source kind (e.g. <c>document</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>The page/locator within the source, if any.</summary>
    public string? Page { get; init; }

    /// <summary>The fused relevance score.</summary>
    public required double Score { get; init; }

    /// <summary>The matched passage content.</summary>
    public required string Content { get; init; }
}
