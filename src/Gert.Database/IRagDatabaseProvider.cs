namespace Gert.Database;

/// <summary>
/// The per-project <c>rag.db</c> seam (tech-stack.md section Architecture / engine
/// portability). Resolves an <see cref="IRagRepository"/> for one
/// <c>(iss, sub, pid)</c>, provisioning + migrating the database on first touch and
/// loading the native sqlite-vec extension so the <c>vec0</c> / FTS5 virtual tables
/// are usable. Lazy and self-provisioning - no separate "ensure" step, no memoised
/// state.
/// </summary>
public interface IRagDatabaseProvider
{
    /// <summary>
    /// Open the RAG repository for one project (open-per-use; the caller disposes
    /// it). The database is created and migrated on first open; identity is already
    /// validated at the API boundary and trusted here.
    /// </summary>
    Task<IRagRepository> OpenAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default);
}
