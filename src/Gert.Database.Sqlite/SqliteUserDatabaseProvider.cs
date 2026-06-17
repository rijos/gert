using Gert.Database;

namespace Gert.Database.Sqlite;

/// <summary>
/// <see cref="IUserDatabaseProvider"/> over per-user <c>user.db</c> files
/// (storage-and-data.md section user.db). Each open self-provisions: the
/// <see cref="SqliteConnectionFactory"/> creates + migrates the database on first
/// open. Addressable by validated <c>(iss, sub)</c> on the request path or by folder
/// key on the admin path (where no token is available).
/// </summary>
public sealed class SqliteUserDatabaseProvider : IUserDatabaseProvider
{
    private const string Family = "user";

    private readonly SqliteDatabasePaths _paths;
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    /// <summary>
    /// Create the provider over the engine's resolved <see cref="SqliteDatabasePaths"/> and
    /// shared connection factory. The clock is injected (dotnet-style-guide.md section 5) and
    /// handed to each repository so tests can pin row timestamps.
    /// </summary>
    public SqliteUserDatabaseProvider(
        SqliteDatabasePaths paths,
        SqliteConnectionFactory factory,
        TimeProvider time)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public async Task<IUserRepository> OpenAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default)
    {
        var connection = await _factory
            .OpenAsync(_paths.UserDb(iss, sub), Family, cancellationToken)
            .ConfigureAwait(false);
        return new SqliteUserRepository(connection, _time);
    }

    /// <inheritdoc />
    public async Task<IUserRepository> OpenByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        // UserDbByKey shape-validates the key (security F6) before any path is formed.
        var connection = await _factory
            .OpenAsync(_paths.UserDbByKey(key), Family, cancellationToken)
            .ConfigureAwait(false);
        return new SqliteUserRepository(connection, _time);
    }

    /// <inheritdoc />
    public Task<bool> DeleteUserAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // This engine's whole-account files: user.db + every project's chat.db. The RAG
        // index (rag.db) is a separate engine and removes its own files; the service
        // orchestrates both.
        var removed = _factory.DeleteDatabaseFiles(_paths.UserDatabaseFiles(iss, sub));
        return Task.FromResult(removed);
    }

    /// <inheritdoc />
    public Task<bool> DeleteUserByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // UserDatabaseFilesByKey shape-validates the key (security F6) before any path is formed.
        var removed = _factory.DeleteDatabaseFiles(_paths.UserDatabaseFilesByKey(key));
        return Task.FromResult(removed);
    }
}
