namespace Gert.Tools.Results;

/// <summary>
/// The model-facing payload of <c>read_document</c>. Either a content window of one document
/// (<see cref="Content"/> + paging fields) or, when the reference was empty/unresolved, the list
/// of <see cref="Available"/> document titles to choose from. Read-only - no side-channels.
/// </summary>
public sealed record ReadDocumentResult
{
    /// <summary>The resolved document title, or null when listing availability.</summary>
    public string? Doc { get; init; }

    /// <summary>The returned text window, or empty when listing / for a binary document.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Total character length of the full document (0 when listing / binary).</summary>
    public int TotalChars { get; init; }

    /// <summary>The character offset this window starts at.</summary>
    public int Offset { get; init; }

    /// <summary>True when more text follows; read again from <see cref="NextOffset"/>.</summary>
    public bool HasMore { get; init; }

    /// <summary>The offset to pass next to continue reading (<see cref="Offset"/> + window length).</summary>
    public int NextOffset { get; init; }

    /// <summary>A human note, e.g. why no content was returned (binary document, unknown name).</summary>
    public string? Note { get; init; }

    /// <summary>The available document titles - populated when listing or when the reference did not resolve.</summary>
    public IReadOnlyList<string>? Available { get; init; }
}
