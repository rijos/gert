namespace Gert.Service.Storage;

/// <summary>
/// THE storage-backend seam: every byte under a user's tree that is not a
/// database file flows through it - uploaded originals (<c>files/...</c>), memory
/// bodies (<c>memory/...</c>), and the JSON config sidecars (<c>meta.json</c>,
/// <c>settings.json</c>) alike (storage-and-data.md section layout). Operations are
/// addressed by an <see cref="ObjectScope"/> (user root or one project root) plus
/// a scope-relative <c>key</c>. Implementations <b>must</b> reject any key that
/// escapes the scope root (<c>..</c> segments or rooted/absolute paths) so a key
/// can never reach another user's or project's bytes.
///
/// <para>
/// Database files (<c>user.db</c>/<c>chat.db</c>/<c>rag.db</c>) are <b>not</b>
/// objects - engines need real local file handles, so they stay with the database
/// providers (<see cref="Database.IUserDatabaseProvider"/>,
/// <see cref="Database.IChatDatabaseProvider"/>, <see cref="Database.IRagDatabaseProvider"/>).
/// </para>
///
/// <para>
/// The local backend is <c>Gert.Storage.LocalObjectStore</c>; an S3/Azure-Blob
/// backend is a drop-in (a new <c>Gert.Storage.*</c> project + one DI swap -
/// nothing above this seam moves).
/// </para>
/// </summary>
public interface IObjectStore
{
    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="key"/> within
    /// <paramref name="scope"/>, creating or overwriting it. The stream is read
    /// from its current position to its end.
    /// <para>
    /// <b>Atomicity contract:</b> a concurrent or subsequent reader observes either
    /// the previous complete object or the new complete object - never a partial
    /// write. Cloud backends give this natively; the local backend stages to a temp
    /// sibling and renames.
    /// </para>
    /// </summary>
    Task PutAsync(
        ObjectScope scope,
        string key,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Open <paramref name="key"/> within <paramref name="scope"/> for reading. The
    /// caller owns and disposes the returned stream. Throws
    /// <see cref="FileNotFoundException"/> if the blob does not exist.
    /// </summary>
    Task<Stream> OpenReadAsync(
        ObjectScope scope,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>True if a blob exists at <paramref name="key"/> within <paramref name="scope"/>.</summary>
    Task<bool> ExistsAsync(
        ObjectScope scope,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete <paramref name="key"/> within <paramref name="scope"/>. Returns
    /// <see langword="true"/> if a blob was removed, <see langword="false"/> if none
    /// existed (idempotent).
    /// </summary>
    Task<bool> DeleteAsync(
        ObjectScope scope,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete every object whose key starts with <paramref name="prefix"/> within
    /// <paramref name="scope"/> - the "forget documents" / empty-project path. An
    /// empty prefix clears all of the scope's objects but keeps the scope root
    /// itself (locally: the directory survives, emptied). Returns the number removed.
    /// </summary>
    Task<int> DeletePrefixAsync(
        ObjectScope scope,
        string prefix,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove the <b>whole scope</b> - every object plus the scope root itself
    /// (locally: <c>rm -rf</c> the directory; cloud: delete-by-prefix). The
    /// user-delete / project-delete path. Returns <see langword="true"/> if any
    /// stored state existed (idempotent).
    /// </summary>
    Task<bool> DeleteScopeAsync(
        ObjectScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List the keys under <paramref name="prefix"/> within <paramref name="scope"/>,
    /// each scope-relative and using <c>/</c> as the separator. An empty prefix
    /// lists all of the scope's objects.
    /// </summary>
    Task<IReadOnlyList<string>> ListAsync(
        ObjectScope scope,
        string prefix,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="ListAsync"/> but with per-object metadata (size,
    /// last-modified) - the admin scan's footprint summary runs on this.
    /// </summary>
    Task<IReadOnlyList<ObjectEntry>> ListEntriesAsync(
        ObjectScope scope,
        string prefix,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerate the user keys (<c>sha256</c> hex folder names) that currently have
    /// any stored state - the admin scan's user enumeration. Local: the directories
    /// under <c>{DataRoot}/users/</c>; S3/Azure: a delimiter listing under the
    /// <c>users/</c> prefix.
    /// </summary>
    Task<IReadOnlyList<string>> ListUserKeysAsync(CancellationToken cancellationToken = default);
}
