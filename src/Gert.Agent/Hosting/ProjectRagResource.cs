using Gert.Chat;
using Gert.Rag;
using Gert.Service.Documents;
using Gert.Tools;
using Gert.Tools.Resources;

namespace Gert.Agent.Hosting;

/// <summary>
/// The project-scoped <see cref="IRagResource"/> (chat-and-tools.md section RAG / hybrid retrieval):
/// embeds the query, opens THIS project's <c>rag.db</c> for the turn's validated <c>(iss, sub, pid)</c>,
/// runs <see cref="IRagStore.HybridSearchAsync"/> (vector KNN + BM25 fused by RRF), and maps each
/// <see cref="RetrievedChunk"/> onto the contracts-level <see cref="RagSearchHit"/> the RAG tool shapes.
/// Pre-scoped to one identity at construction, so a query structurally cannot reach another user's or
/// project's documents - identity is the host's, never the tool's.
/// </summary>
internal sealed class ProjectRagResource : IRagResource
{
    private readonly IRagIndexProvider _databases;
    private readonly IEmbeddingClient _embeddings;
    private readonly string _iss;
    private readonly string _sub;
    private readonly string _pid;

    public ProjectRagResource(
        IRagIndexProvider databases,
        IEmbeddingClient embeddings,
        string iss,
        string sub,
        string pid)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _iss = iss ?? throw new ArgumentNullException(nameof(iss));
        _sub = sub ?? throw new ArgumentNullException(nameof(sub));
        _pid = pid ?? throw new ArgumentNullException(nameof(pid));
    }

    public async Task<IReadOnlyList<RagSearchHit>> SearchAsync(
        RagSearchScope scope,
        string query,
        int k,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.Kind != RagSearchScopeKind.Project)
        {
            throw new NotSupportedException($"unsupported RAG scope: {scope.Kind}");
        }

        var embeddings = await _embeddings.EmbedAsync([query], cancellationToken).ConfigureAwait(false);
        if (embeddings.Count != 1)
        {
            // Contract violation by the embedding client (one vector per input text):
            // throw so the loop's per-call catch surfaces this to the model, rather
            // than silently degrading to a BM25-only search with an empty vector.
            throw new InvalidOperationException(
                $"embedding failed: expected 1 query vector, got {embeddings.Count}");
        }

        await using var store = await _databases
            .OpenAsync(_iss, _sub, _pid, cancellationToken)
            .ConfigureAwait(false);

        var hits = await store.HybridSearchAsync(query, embeddings[0], k, cancellationToken).ConfigureAwait(false);

        var mapped = new List<RagSearchHit>(hits.Count);
        foreach (var hit in hits)
        {
            mapped.Add(new RagSearchHit
            {
                DocId = hit.Document.Id,
                // The filename column is base64 display metadata (StoredFilenames.Encode); decode it.
                Title = StoredFilenames.Decode(hit.Document.Filename),
                Kind = "document",
                Page = hit.Chunk.Page,
                Score = hit.Score,
                Content = hit.Chunk.Content,
            });
        }

        return mapped;
    }
}
