namespace Gert.Database;

/// <summary>
/// The per-user <c>user.db</c> seam - username, settings, and the project registry
/// (storage-and-data.md section user.db). Resolves an <see cref="IUserRepository"/> for a
/// user, provisioning + migrating the database on first open. Lazy and
/// self-provisioning - no "ensure" step, no memoised state.
///
/// <para>
/// Two address forms: <see cref="OpenAsync"/> by the validated <c>(iss, sub)</c> for
/// the request path, and <see cref="OpenByKeyAsync"/> by the opaque folder key for
/// the admin scan, which enumerates users without a token and only ever holds the
/// hashed key.
/// </para>
/// </summary>
public interface IUserDatabaseProvider
{
    /// <summary>
    /// Open the user repository for the caller (open-per-use; the caller disposes).
    /// Created + migrated on first open; identity is trusted past the API boundary.
    /// </summary>
    Task<IUserRepository> OpenAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Open a user repository by folder key (the admin path). The key is the
    /// <c>sha256(iss + "\n" + sub)</c> hex folder name; it is shape-validated
    /// (<c>^[0-9a-f]{64}$</c>) before any path is formed (security F6). The database
    /// is opened read/write and migrated like the request path.
    /// </summary>
    Task<IUserRepository> OpenByKeyAsync(
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Destroy <b>all</b> of the caller's structured database state - <c>user.db</c> plus
    /// every project's <c>chat.db</c> - the database engine's half of an account delete
    /// (the RAG index is a separate engine). The engine owns it: a file-backed
    /// engine drops its pooled handles and removes its database files; a server-backed
    /// engine deletes the user's rows. The artifact half (file blobs) is the
    /// <c>IObjectStore</c>'s; the service orchestrates both and calls this <b>before</b>
    /// the blob delete so a local whole-tree wipe never races an open handle. Returns
    /// <see langword="true"/> if any state existed (idempotent).
    /// </summary>
    Task<bool> DeleteUserAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Destroy all of a user's database state by folder key - the admin delete
    /// (rest-api.md section admin). The key is the <c>sha256(iss + "\n" + sub)</c> hex
    /// folder name, shape-validated (<c>^[0-9a-f]{64}$</c>) before any path is formed
    /// (security F6). Like <see cref="DeleteUserAsync"/> the artifact half is the
    /// <c>IObjectStore</c>'s and the service sequences the two. Returns
    /// <see langword="true"/> if any state existed (idempotent).
    /// </summary>
    Task<bool> DeleteUserByKeyAsync(
        string key,
        CancellationToken cancellationToken = default);
}
