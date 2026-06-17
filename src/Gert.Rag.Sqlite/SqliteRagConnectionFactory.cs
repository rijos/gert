using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Gert.Rag.Sqlite;

/// <summary>
/// Opens and self-provisions the per-project <c>rag.db</c> SQLite connection
/// (storage-and-data.md section connection management / lazy provisioning): create the
/// db file's parent directory, open with WAL + a 5s busy timeout + foreign keys ON,
/// load the native <b>sqlite-vec</b> extension, then apply the <c>rag</c> migrations
/// <i>on the very connection being returned</i>. Self-contained in the RAG engine leaf
/// (deliberately duplicating the small open/unlink mechanics of
/// <c>Gert.Database.Sqlite</c> so the RAG capability has no dependency on the SQL
/// database engine - the two are independent stores).
/// </summary>
public sealed class SqliteRagConnectionFactory
{
    private readonly SqliteRagParameters _parameters;

    /// <summary>Create the factory; <see cref="SqliteRagParameters"/> locates the <c>vec0</c> extension.</summary>
    public SqliteRagConnectionFactory(IOptions<SqliteRagParameters> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _parameters = parameters.Value;
    }

    /// <summary>
    /// Open <paramref name="dbPath"/> (creating it + its directory if absent) with the
    /// sqlite-vec extension loaded, and apply the <c>rag</c> migrations. The caller owns +
    /// disposes the returned connection.
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(string dbPath, CancellationToken cancellationToken)
    {
        // mkdir the parent (idempotent) so ReadWriteCreate can materialise the file.
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(ConnectionString(dbPath));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText =
                    "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // The rag migration family creates vec0 / FTS5 virtual tables, so the extension
            // must be loaded on this connection before the migrations run.
            connection.EnableExtensions(true);
            connection.LoadExtension(ResolveVecExtensionPath());

            await SqliteRagMigrationRunner.ApplyAsync(connection, "rag", cancellationToken).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Drop the pooled connection handles <b>for these files only</b>, then unlink the given
    /// database files (and their <c>-wal</c>/<c>-shm</c> sidecars) - the file-backed engine's
    /// half of a delete. A pooled handle would keep a deleted file alive and resurface stale
    /// rows after re-provisioning, so clear each file's own pool first. We clear per file -
    /// never <c>ClearAllPools()</c> - so deleting one project never disturbs another user's
    /// open connections (isolation, principle #2). Returns <see langword="true"/> if any
    /// database file existed (idempotent).
    /// </summary>
    public bool DeleteDatabaseFiles(IEnumerable<string> dbPaths)
    {
        ArgumentNullException.ThrowIfNull(dbPaths);

        var removed = false;
        foreach (var dbPath in dbPaths)
        {
            // Close just this file's pooled handles (keyed by its connection string),
            // leaving every other database's pool untouched.
            ClearPoolFor(dbPath);

            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
                removed = true;
            }

            foreach (var sidecar in new[] { dbPath + "-wal", dbPath + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }
        }

        return removed;
    }

    /// <summary>The open/delete connection string for <paramref name="dbPath"/> - one definition so the pool key matches.</summary>
    private static string ConnectionString(string dbPath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();

    /// <summary>Clear only the connection pool for <paramref name="dbPath"/> (matched by its connection string).</summary>
    private static void ClearPoolFor(string dbPath)
    {
        using var connection = new SqliteConnection(ConnectionString(dbPath));
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>
    /// The configured <see cref="SqliteRagParameters.VecExtensionPath"/>, or <c>vec0.so</c>
    /// beside the running assembly (the csproj copies the vendored extension into every
    /// consumer's output).
    /// </summary>
    private string ResolveVecExtensionPath() =>
        string.IsNullOrWhiteSpace(_parameters.VecExtensionPath)
            ? Path.Combine(AppContext.BaseDirectory, "vec0.so")
            : _parameters.VecExtensionPath;
}
