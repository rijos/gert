using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Gert.Rag.Sqlite;

/// <summary>
/// Applies the <c>rag</c> database's embedded SQL migrations by <c>PRAGMA user_version</c>
/// (storage-and-data.md section lazy provisioning + migrations): any migration whose numeric
/// version is newer than the DB's <c>user_version</c> is run in order, each inside a
/// transaction that also bumps <c>user_version</c>. Migrations are embedded resources named
/// <c>Migrations/rag/NNN_*.sql</c>. The <c>rag</c> family creates <c>vec0</c> / FTS5 virtual
/// tables, so the connection it runs on must already have the native <b>sqlite-vec</b>
/// extension loaded - <see cref="SqliteRagConnectionFactory"/> loads it before calling here.
/// (A self-contained copy in the RAG engine leaf, so this capability needs no dependency on
/// <c>Gert.Database.Sqlite</c>.)
/// </summary>
public static class SqliteRagMigrationRunner
{
    private const string ResourcePrefix = "Gert.Rag.Sqlite.Migrations.";

    /// <summary>
    /// Apply pending migrations for <paramref name="family"/> (e.g. <c>rag</c>) on an
    /// already-open connection. Idempotent: re-running on an up-to-date DB is a no-op.
    /// Returns the resulting <c>user_version</c>.
    /// </summary>
    public static async Task<int> ApplyAsync(
        SqliteConnection connection,
        string family,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(family);

        var current = await GetUserVersionAsync(connection, null, cancellationToken).ConfigureAwait(false);

        foreach (var (version, resourceName) in DiscoverMigrations(family))
        {
            if (version <= current)
            {
                continue;
            }

            var sql = ReadResource(resourceName);

            // Each migration + its user_version bump is one atomic step under an IMMEDIATE
            // (write-locking) transaction, so concurrent first-request provisioning of the
            // same DB serializes: a second migrator BLOCKS, then RE-READS user_version inside
            // the lock and skips the step the winner already applied. The interpolated version
            // is an int we produced (PRAGMA forbids parameters), so it is safe.
            await using var transaction = connection.BeginTransaction(deferred: false);

            var applied = await GetUserVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (version <= applied)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                current = applied;
                continue;
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText = $"PRAGMA user_version = {version.ToString(CultureInfo.InvariantCulture)};";
                await bump.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            current = version;
        }

        return current;
    }

    private static async Task<int> GetUserVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "PRAGMA user_version;";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Discover embedded migrations for a family, ordered ascending by version.
    /// Resource names look like <c>Gert.Rag.Sqlite.Migrations.rag.001_init.sql</c>.
    /// </summary>
    private static IEnumerable<(int Version, string ResourceName)> DiscoverMigrations(string family)
    {
        var assembly = typeof(SqliteRagMigrationRunner).Assembly;
        var familyPrefix = ResourcePrefix + family + ".";

        var migrations = new List<(int Version, string ResourceName)>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(familyPrefix, StringComparison.Ordinal) ||
                !name.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var tail = name[familyPrefix.Length..];
            var digits = new string(tail.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0 || !int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
            {
                continue;
            }

            migrations.Add((version, name));
        }

        return migrations.OrderBy(m => m.Version);
    }

    private static string ReadResource(string resourceName)
    {
        var assembly = typeof(SqliteRagMigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
