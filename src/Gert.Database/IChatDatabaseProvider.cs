namespace Gert.Database;

/// <summary>
/// The per-project <c>chat.db</c> seam (tech-stack.md section Architecture / engine
/// portability). Resolves an <see cref="IChatRepository"/> for one
/// <c>(iss, sub, pid)</c>, provisioning + migrating the database on first touch
/// (storage-and-data.md section lazy provisioning) on the very connection it opens - no
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

    /// <summary>
    /// Destroy one project's chat database - the DB half of a project delete/empty.
    /// The engine owns this: a file-backed
    /// engine drops its pooled handles and removes <c>chat.db</c>; a server-backed
    /// engine (e.g. Postgres) deletes the project's rows. The artifact half (file/
    /// memory blobs) is the <c>IObjectStore</c>'s; the service orchestrates both and
    /// calls this <b>before</b> the blob delete so a local whole-tree wipe never races
    /// an open handle. Returns <see langword="true"/> if any state existed
    /// (idempotent). Identity is validated at the API boundary and trusted here.
    /// </summary>
    Task<bool> DeleteProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default);
}
