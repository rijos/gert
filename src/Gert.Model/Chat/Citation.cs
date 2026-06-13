namespace Gert.Model.Chat;

/// <summary>
/// A footnote/source marker on an assistant message - mirrors the
/// <c>citations</c> row in a project's <c>chat.db</c> (storage-and-data.md
/// section chat.db).
/// </summary>
public sealed record Citation
{
    /// <summary>UUID primary key.</summary>
    public required string Id { get; init; }

    public required string MessageId { get; init; }

    /// <summary>
    /// Provenance: the <c>tool_calls.id</c> that produced this citation, or null
    /// for citations not attributable to a tool (e.g. model-inline). Keeps the
    /// Message -> ToolCall -> Citations tree intact; display ordinals are computed
    /// at read time.
    /// </summary>
    public string? ToolCallId { get; init; }

    /// <summary>The <c>[1]</c>, <c>[2]</c> marker ordinal.</summary>
    public required int Ordinal { get; init; }

    public required CitationSourceType SourceType { get; init; }

    /// <summary>For document sources: the <c>rag.db documents.id</c>.</summary>
    public string? DocId { get; init; }

    /// <summary>Display label, e.g. "qdrant-benchmarks.pdf - p.4" or a URL title.</summary>
    public required string Label { get; init; }

    /// <summary>Locator within the source - "p.4", "section 3", or a URL.</summary>
    public string? Locator { get; init; }

    public double? Score { get; init; }
}
