using Gert.Database;
using Gert.Storage;
using Microsoft.Extensions.Options;

namespace Gert.Database.Sqlite;

/// <summary>
/// The <c>Sqlite</c> database-engine plugin (<see cref="IDatabaseEngineBuilder"/>): builds the
/// per-user/per-project SQLite providers over the engine's resolved
/// <see cref="SqliteDatabasePaths"/> (<see cref="SqliteDatabaseParameters.DataRoot"/> or the
/// shared <see cref="StorageOptions.DataRoot"/>) + the shared <see cref="SqliteConnectionFactory"/>.
/// Registered keyed by its <see cref="Type"/> in <c>AddGertDatabaseSqlite</c>; the generic
/// <see cref="DatabaseEngineFactory"/> resolves it when <c>Gert:Database:Type</c> is
/// <c>Sqlite</c> (the default) - no central switch over Type.
/// </summary>
public sealed class SqliteDatabaseEngineBuilder : IDatabaseEngineBuilder
{
    private readonly SqliteDatabasePaths _paths;
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public SqliteDatabaseEngineBuilder(
        IOptions<StorageOptions> storage,
        IOptions<SqliteDatabaseParameters> parameters,
        SqliteConnectionFactory factory,
        TimeProvider time)
    {
        _paths = new SqliteDatabasePaths(storage, parameters);
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public string Type => "Sqlite";

    /// <inheritdoc />
    public IUserDatabaseProvider BuildUserDatabaseProvider() =>
        new SqliteUserDatabaseProvider(_paths, _factory, _time);

    /// <inheritdoc />
    public IChatDatabaseProvider BuildChatDatabaseProvider() =>
        new SqliteChatDatabaseProvider(_paths, _factory);
}
