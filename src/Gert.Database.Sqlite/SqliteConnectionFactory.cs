using Microsoft.Data.Sqlite;

namespace Gert.Database.Sqlite;

/// <summary>
/// Opens and self-provisions the SQLite databases this engine owns - <c>user.db</c> and
/// per-project <c>chat.db</c> (storage-and-data.md section connection management / lazy
/// provisioning). Shared by the user/chat providers so the open mechanics live in exactly one
/// place: create the db file's parent directory, open with WAL + a 5s busy timeout + foreign
/// keys ON, then apply the family's migrations <i>on the very connection being returned</i> - so
/// the repository's own connection is the one that was migrated, with no extra opens and no
/// memoised "already provisioned" state to keep coherent. (The RAG index is a separate
/// capability with its own connection factory in <c>Gert.Rag.Sqlite</c> - this engine knows
/// nothing about sqlite-vec.)
///
/// <para>
/// Creating the db file's parent directory is the one filesystem coupling, and it is
/// deliberately a private adapter detail - the database <i>ports</i> know nothing
/// about directories (a server-backed engine wouldn't have any).
/// </para>
/// </summary>
public sealed class SqliteConnectionFactory
{
    /// <summary>
    /// Open <paramref name="dbPath"/> (creating it + its directory if absent) and
    /// apply the <paramref name="family"/> migrations. The caller owns + disposes
    /// the returned connection.
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(
        string dbPath,
        string family,
        CancellationToken cancellationToken)
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

            await SqliteMigrationRunner.ApplyAsync(connection, family, cancellationToken).ConfigureAwait(false);

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
    /// half of a delete. Open-per-use returns connections to Microsoft.Data.Sqlite's internal
    /// pool, so a pooled handle would keep a deleted file alive and resurface stale rows after
    /// re-provisioning; clearing each file's own pool first makes the unlink final. We clear
    /// per file - never <c>ClearAllPools()</c> - so deleting one user/project never disturbs
    /// another user's open connections (isolation, principle #2). The engine owns its own
    /// files; the storage layer never reaches into it. Returns <see langword="true"/> if any
    /// database file existed (idempotent).
    /// </summary>
    public bool DeleteDatabaseFiles(IEnumerable<string> dbPaths)
    {
        ArgumentNullException.ThrowIfNull(dbPaths);

        var removed = false;
        foreach (var dbPath in dbPaths)
        {
            ClearPoolFor(dbPath);

            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
                removed = true;
            }

            // WAL mode leaves -wal/-shm beside the db; take them with it.
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
}
