using Gert.Database;
using Gert.Service.Storage;
using Microsoft.Extensions.Options;

namespace Gert.Database.Sqlite;

/// <summary>
/// <see cref="IUserDatabaseProvider"/> over per-user <c>user.db</c> files
/// (storage-and-data.md § user.db). Each open self-provisions: the
/// <see cref="SqliteConnectionFactory"/> creates + migrates the database on first
/// open. Addressable by validated <c>(iss, sub)</c> on the request path or by folder
/// key on the admin path (where no token is available).
/// </summary>
public sealed class SqliteUserDatabaseProvider : IUserDatabaseProvider
{
    private const string Family = "user";

    private readonly SqliteDatabasePaths _paths;
    private readonly SqliteConnectionFactory _factory;

    /// <summary>Create the provider over the bound <see cref="StorageOptions"/> and shared connection factory.</summary>
    public SqliteUserDatabaseProvider(IOptions<StorageOptions> options, SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _paths = new SqliteDatabasePaths(options);
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
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
        return new SqliteUserRepository(connection);
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
        return new SqliteUserRepository(connection);
    }
}
