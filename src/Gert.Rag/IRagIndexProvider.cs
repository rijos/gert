namespace Gert.Rag;

/// <summary>
/// The per-project RAG index seam (tech-stack.md section Architecture / engine
/// portability). Resolves an <see cref="IRagStore"/> for one <c>(iss, sub, pid)</c>,
/// provisioning the index on first touch. Lazy and self-provisioning - no separate
/// "ensure" step, no memoised state. The engine behind it is config-selected
/// (<c>Gert:Rag:Type</c>); the SQLite impl loads sqlite-vec + migrates <c>rag.db</c>,
/// a dedicated vector store would create/connect to its collection.
/// </summary>
public interface IRagIndexProvider
{
    /// <summary>
    /// Open the RAG store for one project (open-per-use; the caller disposes it). The
    /// index is created/provisioned on first open; identity is already validated at the
    /// API boundary and trusted here.
    /// </summary>
    Task<IRagStore> OpenAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Destroy one project's RAG index - the RAG half of a project delete/empty, and
    /// the "forget my documents" path. The engine owns it: a file-backed engine drops
    /// its pooled handles and removes <c>rag.db</c>; a server-backed store drops the
    /// project's collection/rows. The artifact half (file blobs) is the
    /// <c>IObjectStore</c>'s; the service orchestrates both and calls this <b>before</b>
    /// the blob delete so a local whole-tree wipe never races an open handle. Returns
    /// <see langword="true"/> if any state existed (idempotent). Identity is validated at
    /// the API boundary.
    /// </summary>
    Task<bool> DeleteProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Destroy <b>all</b> of a user's RAG state - every project's index - the RAG half of
    /// an account delete. The RAG engine is independent of the structured-database engine
    /// and may live on its own volume, so each owns deleting only its own files; the service
    /// orchestrates both (plus the <c>IObjectStore</c> blob half). Returns
    /// <see langword="true"/> if any state existed (idempotent). Identity is validated at the
    /// API boundary.
    /// </summary>
    Task<bool> DeleteUserAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Destroy all of a user's RAG state by folder key - the admin delete. The key is the
    /// <c>sha256(iss + "\n" + sub)</c> hex folder name, shape-validated (<c>^[0-9a-f]{64}$</c>)
    /// before any path is formed (security F6). Like <see cref="DeleteUserAsync"/> the service
    /// sequences this with the structured-database and blob halves. Returns
    /// <see langword="true"/> if any state existed (idempotent).
    /// </summary>
    Task<bool> DeleteUserByKeyAsync(
        string key,
        CancellationToken cancellationToken = default);
}
