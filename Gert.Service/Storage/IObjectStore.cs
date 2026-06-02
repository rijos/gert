namespace Gert.Service.Storage;

/// <summary>
/// The object-storage seam for per-project file blobs — uploaded originals under
/// <c>projects/{pid}/files/</c> and, later, exports (storage-and-data.md § layout).
/// All operations are project-scoped via an <see cref="ObjectScope"/> and address
/// a blob by an opaque, project-relative <c>key</c> (e.g. <c>upload-1.pdf</c> or
/// <c>exports/decision.md</c>). Implementations <b>must</b> reject any key that
/// escapes the scope's <c>files/</c> root (<c>..</c> segments or rooted/absolute
/// paths) so a key can never reach another project's bytes.
///
/// <para>
/// The local-persistence adapter (<c>Gert.Database.Sqlite.LocalObjectStore</c>)
/// writes under the filesystem. A future object-storage backend is a drop-in:
/// </para>
/// <code>// S3: new IObjectStore impl, one DI registration</code>
/// <para>
/// (a new <c>Gert.Storage.S3</c> project + a single DI swap — nothing else moves).
/// </para>
/// </summary>
public interface IObjectStore
{
    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="key"/> within
    /// <paramref name="scope"/>, creating or overwriting it. Parent directories are
    /// created as needed. The stream is read from its current position to its end.
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
    /// Delete every blob whose key starts with <paramref name="prefix"/> within
    /// <paramref name="scope"/> — the "forget documents" / project-delete path. An
    /// empty prefix clears all of the scope's blobs. Returns the number removed.
    /// </summary>
    Task<int> DeletePrefixAsync(
        ObjectScope scope,
        string prefix,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List the keys under <paramref name="prefix"/> within <paramref name="scope"/>,
    /// each project-relative and using <c>/</c> as the separator. An empty prefix
    /// lists all of the scope's blobs.
    /// </summary>
    Task<IReadOnlyList<string>> ListAsync(
        ObjectScope scope,
        string prefix,
        CancellationToken cancellationToken = default);
}
