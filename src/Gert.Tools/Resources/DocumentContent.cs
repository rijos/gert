namespace Gert.Tools.Resources;

/// <summary>
/// A slice of one document's full text (chat-and-tools.md section read_document). The host reads
/// the <b>original stored blob</b> and decodes it as UTF-8 - exact bytes, not reassembled RAG
/// chunks (which are lossy) - returning a window the model can page through with
/// <see cref="Offset"/> + <see cref="HasMore"/>. A binary document (pdf/docx/xlsx) is not text:
/// <see cref="IsText"/> is false and <see cref="Content"/> is empty (the model should fall back to
/// <c>search_documents</c>).
/// </summary>
public sealed record DocumentContent
{
    /// <summary>The resolved document title (decoded original filename).</summary>
    public required string Title { get; init; }

    /// <summary>False for a binary document whose bytes are not text; then <see cref="Content"/> is empty.</summary>
    public required bool IsText { get; init; }

    /// <summary>The returned text window, <c>[Offset, Offset + Content.Length)</c> of the full text.</summary>
    public required string Content { get; init; }

    /// <summary>The total character length of the full decoded document.</summary>
    public required int TotalChars { get; init; }

    /// <summary>The character offset this window starts at.</summary>
    public required int Offset { get; init; }

    /// <summary>True when more text follows this window (the model should read again from <see cref="Offset"/> + length).</summary>
    public required bool HasMore { get; init; }
}
