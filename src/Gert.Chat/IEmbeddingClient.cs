namespace Gert.Chat;

/// <summary>
/// Port for the text-embedding model (chat-and-tools.md section ingestion / hybrid
/// retrieval). The real client (vLLM embeddings) lives in <c>Gert.Chat</c>
/// tests use a deterministic fake. Vectors must match the index
/// dimension (bge-m3 = 1024).
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>Embed a batch of texts -> one vector each, in input order.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
