using System.Globalization;
using FluentAssertions;
using Gert.Database.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>Chat migration runner against a real on-disk SQLite database.</summary>
public class MigrationTests
{
    [Fact]
    public async Task Chat_migration_applies_from_empty_and_bumps_user_version()
    {
        var dbPath = TempDbPath();
        try
        {
            await using var connection = await OpenAsync(dbPath);

            (await UserVersionAsync(connection)).Should().Be(0);

            var applied = await SqliteMigrationRunner.ApplyAsync(connection, "chat");

            applied.Should().Be(4);
            (await UserVersionAsync(connection)).Should().Be(4);

            var tables = await TableNamesAsync(connection);
            tables.Should().Contain(new[] { "conversations", "messages", "tool_calls", "citations", "artifacts", "turn_events" });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Chat_migration_is_idempotent()
    {
        var dbPath = TempDbPath();
        try
        {
            await using var connection = await OpenAsync(dbPath);

            await SqliteMigrationRunner.ApplyAsync(connection, "chat");
            var second = await SqliteMigrationRunner.ApplyAsync(connection, "chat");

            second.Should().Be(4, "re-running an up-to-date DB is a no-op");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Chat_migration_upgrades_v1_database_in_place()
    {
        var dbPath = TempDbPath();
        try
        {
            await using var connection = await OpenAsync(dbPath);

            // Build a real v1 database from the embedded 001 script, with one
            // pre-pipeline conversation + message in it.
            await ExecuteAsync(connection, ReadEmbeddedMigration("001_init.sql"));
            await ExecuteAsync(connection, "PRAGMA user_version = 1;");
            await ExecuteAsync(connection,
                "INSERT INTO conversations (id, title, model_id, created_at, updated_at) " +
                "VALUES ('c1', 'legacy', 'm', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');");
            await ExecuteAsync(connection,
                "INSERT INTO messages (id, conversation_id, role, content, created_at) " +
                "VALUES ('m1', 'c1', 'user', 'hi', '2026-01-01T00:00:00Z');");

            var applied = await SqliteMigrationRunner.ApplyAsync(connection, "chat");

            applied.Should().Be(4);

            // Legacy rows got the v2 defaults: seq=0, status='complete', next_seq=1.
            (await ScalarAsync(connection, "SELECT seq FROM messages WHERE id='m1';")).Should().Be(0L);
            (await ScalarAsync(connection, "SELECT status FROM messages WHERE id='m1';")).Should().Be("complete");
            (await ScalarAsync(connection, "SELECT next_seq FROM conversations WHERE id='c1';")).Should().Be(1L);
            (await ScalarAsync(connection, "SELECT COUNT(*) FROM turn_events;")).Should().Be(0L);

            // …and the v3 columns exist, NULL on legacy rows (reasoning/metrics
            // + the conversation reasoning toggles).
            (await ScalarAsync(connection, "SELECT reasoning FROM messages WHERE id='m1';")).Should().Be(DBNull.Value);
            (await ScalarAsync(connection, "SELECT duration_ms FROM messages WHERE id='m1';")).Should().Be(DBNull.Value);
            (await ScalarAsync(connection, "SELECT context_tokens FROM messages WHERE id='m1';")).Should().Be(DBNull.Value);
            (await ScalarAsync(connection, "SELECT thinking FROM conversations WHERE id='c1';")).Should().Be(DBNull.Value);
            (await ScalarAsync(connection, "SELECT preserve_thinking FROM conversations WHERE id='c1';")).Should().Be(DBNull.Value);

            // …and the v4 attachments column exists, NULL on legacy rows.
            (await ScalarAsync(connection, "SELECT attachments_json FROM messages WHERE id='m1';")).Should().Be(DBNull.Value);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    private static string ReadEmbeddedMigration(string fileName)
    {
        var assembly = typeof(SqliteMigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            $"Gert.Database.Sqlite.Migrations.chat.{fileName}")
            ?? throw new InvalidOperationException($"Embedded migration '{fileName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static string TempDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "gert-tests", $"mig-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> UserVersionAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> TableNamesAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
