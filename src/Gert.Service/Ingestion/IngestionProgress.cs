namespace Gert.Service.Ingestion;

/// <summary>Progress for the doclist pill / "embedding n / m chunks..." hint.</summary>
public sealed record IngestionProgress
{
    public required int ChunksEmbedded { get; init; }

    public required int ChunksTotal { get; init; }
}
