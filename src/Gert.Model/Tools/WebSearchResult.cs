namespace Gert.Model.Tools;

/// <summary>One web result - becomes a web-type citation.</summary>
public sealed record WebSearchResult
{
    public required string Title { get; init; }

    public required string Url { get; init; }

    /// <summary>Snippet / summary, if fetched.</summary>
    public string? Snippet { get; init; }
}
