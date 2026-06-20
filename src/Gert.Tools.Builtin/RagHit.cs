namespace Gert.Tools.Builtin;

/// <summary>
/// One fused hit in a <see cref="RagResult"/> - the ordinal that ties it to its
/// citation, the decoded source name, the source kind (<c>memory</c>/<c>document</c>),
/// the page locator, the rounded RRF score, and the passage content.
/// </summary>
public sealed record RagHit
{
    public required int Ordinal { get; init; }

    public required string Doc { get; init; }

    public required string Kind { get; init; }

    public string? Page { get; init; }

    public required double Score { get; init; }

    public required string Content { get; init; }
}
