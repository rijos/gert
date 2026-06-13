using Gert.Database;
using Gert.Service.Storage;
using Microsoft.Extensions.Options;

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
    /// Create the provider over the bound <see cref="StorageOptions"/> and shared
    /// connection factory. The clock is injected (dotnet-style-guide.md section 5) and
    /// handed to each repository so tests can pin row timestamps.
    /// </summary>
    public SqliteUserDatabaseProvider(
        IOptions<StorageOptions> options,
        SqliteConnectionFactory factory,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _paths = new SqliteDatabasePaths(options);
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
}
