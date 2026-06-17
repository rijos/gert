namespace Gert.Rag.Sqlite;

/// <summary>
/// <see cref="IRagIndexProvider"/> over per-project <c>rag.db</c> files
/// (storage-and-data.md section rag.db). Each open self-provisions and loads the native
/// <b>sqlite-vec</b> extension before migrating, so the <c>vec0</c> / FTS5 virtual
/// tables in the rag migration family can be created and queried. The path is the
/// scope - a query cannot reach another project's rows.
/// </summary>
public sealed class SqliteRagIndexProvider : IRagIndexProvider
{
    private readonly SqliteRagPaths _paths;
    private readonly SqliteRagConnectionFactory _factory;

    /// <summary>Create the provider over the engine's resolved <see cref="SqliteRagPaths"/> and the RAG connection factory.</summary>
    public SqliteRagIndexProvider(SqliteRagPaths paths, SqliteRagConnectionFactory factory)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public async Task<IRagStore> OpenAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        var connection = await _factory
            .OpenAsync(_paths.RagDb(iss, sub, pid), cancellationToken)
            .ConfigureAwait(false);
        return new SqliteRagStore(connection);
    }

    /// <inheritdoc />
    public Task<bool> DeleteProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // RagDb resolves through ProjectRoot, which shape-validates the pid and asserts
        // the path stays under the user root (F12) before any unlink.
        var removed = _factory.DeleteDatabaseFiles([_paths.RagDb(iss, sub, pid)]);
        return Task.FromResult(removed);
    }

    /// <inheritdoc />
    public Task<bool> DeleteUserAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Every project's rag.db under the user root - the RAG half of an account delete.
        var removed = _factory.DeleteDatabaseFiles(_paths.UserRagDatabaseFiles(iss, sub));
        return Task.FromResult(removed);
    }

    /// <inheritdoc />
    public Task<bool> DeleteUserByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // UserRagDatabaseFilesByKey shape-validates the key (security F6) before any path is formed.
        var removed = _factory.DeleteDatabaseFiles(_paths.UserRagDatabaseFilesByKey(key));
        return Task.FromResult(removed);
    }
}
