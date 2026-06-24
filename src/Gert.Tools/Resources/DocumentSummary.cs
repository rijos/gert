namespace Gert.Tools.Resources;

/// <summary>
/// Metadata for one ready document a tool may read in full (chat-and-tools.md section read_document):
/// the decoded title plus enough to identify and size it. A contracts-level shape - the host maps
/// its internal document row onto it. Distinct from a <see cref="RagSearchHit"/> (a search passage):
/// this is the whole-document handle the model resolves a <c>read_document</c> call against.
/// </summary>
public sealed record DocumentSummary
{
    /// <summary>The document's id (server-generated UUID).</summary>
    public required string Id { get; init; }

    /// <summary>The human-readable title (decoded original filename).</summary>
    public required string Title { get; init; }

    /// <summary>The upload content-type, as supplied.</summary>
    public required string Mime { get; init; }

    /// <summary>The stored blob size in bytes.</summary>
    public required long SizeBytes { get; init; }
}
