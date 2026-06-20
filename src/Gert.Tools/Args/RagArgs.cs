namespace Gert.Tools;

/// <summary>
/// Arguments for the RAG tool (<c>search_documents</c>): the natural-language
/// <see cref="Query"/> and an optional top-k. The wire is snake_case (<c>k</c>);
/// the validator range-checks a supplied <see cref="K"/> to [1, 20] (an
/// out-of-range value is a model-correctable error) and the tool defaults it to 8
/// when omitted.
/// </summary>
public sealed record RagArgs
{
    /// <summary>The natural-language search query (required).</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>How many passages to return (1-20); null defaults to 8.</summary>
    public int? K { get; init; }
}
