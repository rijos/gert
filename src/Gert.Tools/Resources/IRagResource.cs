namespace Gert.Tools;

/// <summary>
/// Read-only hybrid search over a project's RAG index (chat-and-tools.md section RAG), pre-scoped
/// by the host. The tool passes a <see cref="RagSearchScope"/> and gets ranked
/// <see cref="RagSearchHit"/>s it shapes into the model payload + citations - it never opens a
/// store or embeds a query itself (the host owns the embedding client and the scoped index).
/// </summary>
public interface IRagResource
{
    /// <summary>Return up to <paramref name="k"/> ranked hits for <paramref name="query"/> in the scope.</summary>
    Task<IReadOnlyList<RagSearchHit>> SearchAsync(
        RagSearchScope scope,
        string query,
        int k,
        CancellationToken cancellationToken = default);
}
