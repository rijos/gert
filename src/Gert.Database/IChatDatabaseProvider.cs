namespace Gert.Database;

/// <summary>
/// The per-project <c>chat.db</c> seam (tech-stack.md § Architecture / engine
/// portability). Resolves an <see cref="IChatRepository"/> for one
/// <c>(iss, sub, pid)</c>, provisioning + migrating the database on first touch
/// (storage-and-data.md § lazy provisioning) on the very connection it opens — no
/// separate "ensure" step, no memoised state. The service layer talks only to the
/// returned repository, never to a connection or engine type.
/// </summary>
public interface IChatDatabaseProvider
{
    /// <summary>
    /// Open the chat repository for one project (open-per-use; the caller disposes
    /// it). The database is created and migrated to the current schema on first
    /// open; identity is already validated at the API boundary and trusted here.
    /// </summary>
    Task<IChatRepository> OpenAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default);
}
