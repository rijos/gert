using System.Globalization;
using System.Text.Json;
using Dapper;
using Gert.Database;
using Gert.Model.Projects;
using Microsoft.Data.Sqlite;

namespace Gert.Database.Sqlite;

/// <summary>
/// Dapper-backed <see cref="IUserRepository"/> over one user's <c>user.db</c>
/// (storage-and-data.md § user.db). Wraps a single open connection opened by the
/// provider (open-per-use); dispose closes it. The connection's path is the scope —
/// there is no <c>(iss, sub)</c> argument, so a query cannot reach another user's
/// rows.
///
/// <para>
/// <c>user_meta</c> and <c>settings</c> are single-row tables (id pinned to 1);
/// <see cref="UserSettings"/> and <see cref="ProjectMeta.Defaults"/> persist as JSON
/// blobs (Web naming, matching the wire) so the schema never has to track their
/// fields. Timestamps persist as round-trippable ISO-8601 (<c>o</c>) UTC text.
/// </para>
/// </summary>
public sealed class SqliteUserRepository : IUserRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    static SqliteUserRepository()
    {
        // Process-wide Dapper config — one owner (DapperBootstrap), three citers.
        DapperBootstrap.EnsureConfigured();
    }

    private readonly SqliteConnection _connection;
    private readonly TimeProvider _time;

    /// <summary>
    /// Wrap an already-open, migrated <c>user.db</c> connection. The clock is
    /// injected (dotnet-style-guide.md §5) so tests can pin <c>created_at</c>.
    /// </summary>
    public SqliteUserRepository(SqliteConnection connection, TimeProvider time)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    // ---- user meta ---------------------------------------------------------

    /// <inheritdoc />
    public async Task<string?> GetUsernameAsync(CancellationToken cancellationToken = default) =>
        await _connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT username FROM user_meta WHERE id = 1;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <inheritdoc />
    public Task SetUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(username);
        const string sql =
            "INSERT INTO user_meta (id, username, created_at) VALUES (1, @username, @createdAt) " +
            "ON CONFLICT(id) DO UPDATE SET username = excluded.username;";
        return _connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { username, createdAt = _time.GetUtcNow().ToString("o", CultureInfo.InvariantCulture) },
            cancellationToken: cancellationToken));
    }

    // ---- settings ----------------------------------------------------------

    /// <inheritdoc />
    public async Task<UserSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT settings_json FROM settings WHERE id = 1;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return new UserSettings();
        }

        return JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new UserSettings();
    }

    /// <inheritdoc />
    public Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        const string sql =
            "INSERT INTO settings (id, settings_json) VALUES (1, @json) " +
            "ON CONFLICT(id) DO UPDATE SET settings_json = excluded.settings_json;";
        return _connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { json = JsonSerializer.Serialize(settings, JsonOptions) },
            cancellationToken: cancellationToken));
    }

    // ---- project registry --------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectMeta>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<ProjectRow>(new CommandDefinition(
            "SELECT id, name, description, instructions, defaults_json, created_at, updated_at " +
            "FROM projects ORDER BY created_at;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(Map).ToList();
    }

    /// <inheritdoc />
    public async Task<ProjectMeta?> GetProjectAsync(string pid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pid);
        var row = await _connection.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            "SELECT id, name, description, instructions, defaults_json, created_at, updated_at " +
            "FROM projects WHERE id = @pid;",
            new { pid },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public Task SaveProjectAsync(ProjectMeta meta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        const string sql =
            "INSERT INTO projects (id, name, description, instructions, defaults_json, created_at, updated_at) " +
            "VALUES (@Id, @Name, @Description, @Instructions, @DefaultsJson, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT(id) DO UPDATE SET " +
            "name = excluded.name, description = excluded.description, " +
            "instructions = excluded.instructions, defaults_json = excluded.defaults_json, " +
            "updated_at = excluded.updated_at;";

        return _connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                meta.Id,
                meta.Name,
                meta.Description,
                meta.Instructions,
                DefaultsJson = meta.Defaults is null ? null : JsonSerializer.Serialize(meta.Defaults, JsonOptions),
                CreatedAt = meta.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
                UpdatedAt = meta.UpdatedAt.ToString("o", CultureInfo.InvariantCulture),
            },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task<bool> DeleteProjectAsync(string pid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pid);
        var rows = await _connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM projects WHERE id = @pid;",
            new { pid },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    // ---- helpers -----------------------------------------------------------

    private static ProjectMeta Map(ProjectRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Description = row.Description,
        Instructions = row.Instructions,
        Defaults = string.IsNullOrWhiteSpace(row.DefaultsJson)
            ? null
            : JsonSerializer.Deserialize<ProjectDefaults>(row.DefaultsJson, JsonOptions),
        CreatedAt = DateTimeOffset.Parse(row.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedAt = DateTimeOffset.Parse(row.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };

    private sealed class ProjectRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public string? Instructions { get; init; }
        public string? DefaultsJson { get; init; }
        public string CreatedAt { get; init; } = string.Empty;
        public string UpdatedAt { get; init; } = string.Empty;
    }
}
