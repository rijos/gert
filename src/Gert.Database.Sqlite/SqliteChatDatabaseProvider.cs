using Gert.Database;
using Gert.Service.Storage;
using Microsoft.Extensions.Options;

namespace Gert.Database.Sqlite;

/// <summary>
/// <see cref="IChatDatabaseProvider"/> over per-project <c>chat.db</c> files
/// (storage-and-data.md § chat.db). Each open self-provisions: the
/// <see cref="SqliteConnectionFactory"/> creates + migrates the database on the
/// connection it returns, which the <see cref="SqliteChatRepository"/> then wraps.
/// The path is the scope — there is no project/user argument on the repository, so a
/// query cannot reach another project's rows.
/// </summary>
public sealed class SqliteChatDatabaseProvider : IChatDatabaseProvider
{
    private readonly SqliteDatabasePaths _paths;
    private readonly SqliteConnectionFactory _factory;

    /// <summary>Create the provider over the bound <see cref="StorageOptions"/> and shared connection factory.</summary>
    public SqliteChatDatabaseProvider(IOptions<StorageOptions> options, SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _paths = new SqliteDatabasePaths(options);
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
}
