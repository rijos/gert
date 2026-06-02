using Gert.Model.Rag;

namespace Gert.Service.Database;

/// <summary>
/// A fused hybrid-search hit — the <see cref="Chunk"/> joined back to its
/// <see cref="Document"/>, with the RRF score that seeds a citation.
/// </summary>
public sealed record RetrievedChunk
{
    public required Chunk Chunk { get; init; }

    public required Document Document { get; init; }

    /// <summary>The fused RRF score (the 0.89 / 0.81 the mockup shows).</summary>
    public required double Score { get; init; }
}
