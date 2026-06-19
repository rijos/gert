using Gert.Database;

namespace Gert.Database.Sqlite;

/// <summary>
/// <see cref="IChatDatabaseProvider"/> over per-project <c>chat.db</c> files
/// (storage-and-data.md section chat.db). Each open self-provisions: the
/// <see cref="SqliteConnectionFactory"/> creates + migrates the database on the
/// connection it returns, which the <see cref="SqliteChatRepository"/> then wraps.
/// The path is the scope - there is no project/user argument on the repository, so a
/// query cannot reach another project's rows.
/// </summary>
public sealed class SqliteChatDatabaseProvider : IChatDatabaseProvider
{
    private readonly SqliteDatabasePaths _paths;
    private readonly SqliteConnectionFactory _factory;

    public SqliteChatDatabaseProvider(SqliteDatabasePaths paths, SqliteConnectionFactory factory)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public async Task<IChatRepository> OpenAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        var connection = await _factory
            .OpenAsync(_paths.ChatDb(iss, sub, pid), "chat", cancellationToken)
            .ConfigureAwait(false);
        return new SqliteChatRepository(connection);
    }

    /// <inheritdoc />
    public Task<bool> DeleteProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // ChatDb resolves through ProjectRoot, which shape-validates the pid and asserts
        // the path stays under the user root (F12) before any unlink.
        var removed = _factory.DeleteDatabaseFiles([_paths.ChatDb(iss, sub, pid)]);
        return Task.FromResult(removed);
    }
}
