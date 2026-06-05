namespace Gert.Service.External;

/// <summary>
/// Port for the text-embedding model (chat-and-tools.md § ingestion / hybrid
/// retrieval). The real client (vLLM embeddings) lives in <c>Gert.External</c>
/// (U10); tests use a deterministic fake. Vectors must match the index
/// dimension (bge-m3 = 1024).
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>Embed a batch of texts → one vector each, in input order.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
