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

            applied.Should().Be(1);
            (await UserVersionAsync(connection)).Should().Be(1);

            var tables = await TableNamesAsync(connection);
            tables.Should().Contain(new[] { "conversations", "messages", "tool_calls", "citations", "artifacts", "turn_events" });

            // The atomic turn gate (decisions section 11): the partial unique index.
            (await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_messages_streaming';"))
                .Should().Be(1L);

            // Sampling and thinking ride the provider, not the conversation row;
            // the response-side reasoning column is per-message.
            var conversationColumns = await ColumnNamesAsync(connection, "conversations");
            conversationColumns.Should().NotContain("params_json");
            conversationColumns.Should().Contain("next_seq");

            var messageColumns = await ColumnNamesAsync(connection, "messages");
            messageColumns.Should().Contain(new[] { "seq", "status", "reasoning", "duration_ms", "context_tokens", "attachments_json" });
        }
        finally
        {
            ClearPoolAndDelete(dbPath);
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

            second.Should().Be(1, "re-running an up-to-date DB is a no-op");
        }
        finally
        {
            ClearPoolAndDelete(dbPath);
        }
    }

    /// <summary>
    /// Release just THIS db file's pooled handle, then delete it. Per-file (not
    /// <c>ClearAllPools()</c>) so a parallel test class's open connections are never disposed
    /// out from under it - the source of a cross-collection flake.
    /// </summary>
    private static void ClearPoolAndDelete(string dbPath)
    {
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            SqliteConnection.ClearPool(connection);
        }

        File.Delete(dbPath);
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

    private static async Task<IReadOnlyList<string>> ColumnNamesAsync(SqliteConnection connection, string table)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk.
            names.Add(reader.GetString(1));
        }

        return names;
    }
}
