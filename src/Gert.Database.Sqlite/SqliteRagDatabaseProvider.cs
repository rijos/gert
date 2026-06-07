using Gert.Database;
using Gert.Service.Storage;
using Microsoft.Extensions.Options;

namespace Gert.Database.Sqlite;

/// <summary>
/// <see cref="IRagDatabaseProvider"/> over per-project <c>rag.db</c> files
/// (storage-and-data.md § rag.db). Each open self-provisions and loads the native
/// <b>sqlite-vec</b> extension before migrating, so the <c>vec0</c> / FTS5 virtual
/// tables in the rag migration family can be created and queried. The path is the
/// scope — a query cannot reach another project's rows.
/// </summary>
public sealed class SqliteRagDatabaseProvider : IRagDatabaseProvider
{
    private readonly SqliteDatabasePaths _paths;
    private readonly SqliteConnectionFactory _factory;

    /// <summary>Create the provider over the bound <see cref="StorageOptions"/> and shared connection factory.</summary>
    public SqliteRagDatabaseProvider(IOptions<StorageOptions> options, SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _paths = new SqliteDatabasePaths(options);
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public async Task<IRagRepository> OpenAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        var connection = await _factory
            .OpenWithVecAsync(_paths.RagDb(iss, sub, pid), "rag", cancellationToken)
            .ConfigureAwait(false);
        return new SqliteRagRepository(connection);
    }
}
