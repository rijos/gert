using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Gert.Database.Sqlite;

/// <summary>
/// Applies a database's embedded SQL migrations by <c>PRAGMA user_version</c>
/// (storage-and-data.md § lazy provisioning + migrations): any migration whose
/// numeric version is newer than the DB's <c>user_version</c> is run in order,
/// each inside a transaction that also bumps <c>user_version</c>. Migrations are
/// embedded resources named <c>Migrations/{family}/NNN_*.sql</c>.
///
/// <para>
/// Both the <c>chat</c> and <c>rag</c> families are applied (per-DB). The <c>rag</c>
/// family creates <c>vec0</c> / FTS5 virtual tables, so the connection it runs on
/// must already have the native <b>sqlite-vec</b> extension loaded — the provider
/// loads it in <c>OpenRagConnectionAsync</c> before calling here.
/// </para>
/// </summary>
public static class SqliteMigrationRunner
{
    private const string ResourcePrefix = "Gert.Database.Sqlite.Migrations.";

    /// <summary>
    /// Apply pending migrations for <paramref name="family"/> (e.g. <c>chat</c>) on
    /// an already-open connection. Idempotent: re-running on an up-to-date DB is a
    /// no-op. Returns the resulting <c>user_version</c>.
    /// </summary>
    public static async Task<int> ApplyAsync(
        SqliteConnection connection,
        string family,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(family);

        var current = await GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        foreach (var (version, resourceName) in DiscoverMigrations(family))
        {
            if (version <= current)
            {
                continue;
            }

            var sql = ReadResource(resourceName);

            // Each migration + its user_version bump is one atomic step. SQLite
            // does not allow PRAGMA user_version to be parameterised, but the value
            // is an int we produced, so interpolation is safe here.
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Discover embedded migrations for a family, ordered ascending by version.
    /// Resource names look like <c>Gert.Database.Sqlite.Migrations.chat.001_init.sql</c>.
    /// </summary>
    private static IEnumerable<(int Version, string ResourceName)> DiscoverMigrations(string family)
    {
        var assembly = typeof(SqliteMigrationRunner).Assembly;
        var familyPrefix = ResourcePrefix + family + ".";

        var migrations = new List<(int Version, string ResourceName)>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(familyPrefix, StringComparison.Ordinal) ||
                !name.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            // "<familyPrefix>001_init.sql" -> leading digits "001".
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
        var assembly = typeof(SqliteMigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
