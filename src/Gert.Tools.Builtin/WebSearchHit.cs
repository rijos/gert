namespace Gert.Tools.Builtin;

/// <summary>One row in a <see cref="WebSearchToolResult"/> - ordinal, title, url, and snippet.</summary>
public sealed record WebSearchHit
{
    public required int Ordinal { get; init; }

    public required string Title { get; init; }

    public required string Url { get; init; }

    public string? Snippet { get; init; }
}
