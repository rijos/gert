using Gert.Service.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Gert.Database.Sqlite;

/// <summary>
/// Opens and self-provisions SQLite connections for every Gert database
/// (storage-and-data.md § connection management / lazy provisioning). Shared by the
/// chat/rag/user providers so the open mechanics live in exactly one place: create
/// the db file's parent directory, open with WAL + a 5s busy timeout + foreign keys
/// ON, optionally load the native <b>sqlite-vec</b> extension (rag only), then apply
/// the family's migrations <i>on the very connection being returned</i> — so the
/// repository's own connection is the one that was migrated, with no extra opens and
/// no memoised "already provisioned" state to keep coherent.
///
/// <para>
/// Creating the db file's parent directory is the one filesystem coupling, and it is
/// deliberately a private adapter detail — the database <i>ports</i> know nothing
/// about directories (a server-backed engine wouldn't have any).
/// </para>
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly SqliteVecOptions _vecOptions;

    /// <summary>Create the factory; <see cref="SqliteVecOptions"/> locates the <c>vec0</c> extension for rag opens.</summary>
    public SqliteConnectionFactory(IOptions<SqliteVecOptions> vecOptions)
    {
        ArgumentNullException.ThrowIfNull(vecOptions);
        _vecOptions = vecOptions.Value;
    }

    /// <summary>
    /// Open <paramref name="dbPath"/> (creating it + its directory if absent) and
    /// apply the <paramref name="family"/> migrations. The caller owns + disposes
    /// the returned connection.
    /// </summary>
    public Task<SqliteConnection> OpenAsync(string dbPath, string family, CancellationToken cancellationToken) =>
        OpenCoreAsync(dbPath, family, loadVec: false, cancellationToken);

    /// <summary>
    /// As <see cref="OpenAsync"/>, but loads the <b>sqlite-vec</b> extension before
    /// migrating — required for the <c>rag</c> family's <c>vec0</c> / FTS5 tables.
    /// </summary>
    public Task<SqliteConnection> OpenWithVecAsync(string dbPath, string family, CancellationToken cancellationToken) =>
        OpenCoreAsync(dbPath, family, loadVec: true, cancellationToken);

    private async Task<SqliteConnection> OpenCoreAsync(
        string dbPath,
        string family,
        bool loadVec,
        CancellationToken cancellationToken)
    {
        // mkdir the parent (idempotent) so ReadWriteCreate can materialise the file.
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText =
                    "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (loadVec)
            {
                connection.EnableExtensions(true);
                connection.LoadExtension(ResolveVecExtensionPath());
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
    /// The configured <see cref="SqliteVecOptions.VecExtensionPath"/>, or
    /// <c>vec0.so</c> beside the running assembly (the csproj copies the vendored
    /// extension into every consumer's output).
    /// </summary>
    private string ResolveVecExtensionPath() =>
        string.IsNullOrWhiteSpace(_vecOptions.VecExtensionPath)
            ? Path.Combine(AppContext.BaseDirectory, "vec0.so")
            : _vecOptions.VecExtensionPath;
}
