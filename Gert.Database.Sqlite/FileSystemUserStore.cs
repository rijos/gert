using System.Text.Json;
using Gert.Model.Projects;
using Gert.Service.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Gert.Database.Sqlite;

/// <summary>
/// Filesystem <see cref="IUserStore"/> — the local-persistence adapter that owns
/// <see cref="UserPaths"/> / <c>DataRoot</c>. It reads/writes the two config files
/// (<c>settings.json</c>, <c>projects/{pid}/meta.json</c>) with direct file I/O —
/// these are config, not user blobs — and performs the coarse directory lifecycle
/// (<c>rm -rf</c> a project/user folder, enumerate users) as plain filesystem ops.
///
/// <para>
/// Identity is handled exactly as the database / object-store seams: <c>(iss, sub)</c>
/// are token-derived and <c>pid</c> is validated by <see cref="UserPaths"/>, so a
/// call can only resolve under the caller's own folder. The admin
/// <see cref="DeleteUserByKeyAsync"/> additionally asserts the resolved path stays
/// <b>under</b> <c>{DataRoot}/users</c> before any deletion (security F6),
/// defence-in-depth on top of the controller's <c>^[0-9a-f]{64}$</c> shape guard.
/// </para>
/// </summary>
public sealed class FileSystemUserStore : IUserStore
{
    // Match the provider's serialization so meta.json/settings.json round-trip
    // byte-for-byte regardless of which component last wrote them.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly StorageOptions _options;
    private readonly UserPaths _paths;

    /// <summary>Create the store with bound <see cref="StorageOptions"/>.</summary>
    public FileSystemUserStore(IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.DataRoot))
        {
            throw new InvalidOperationException(
                $"{nameof(StorageOptions)}.{nameof(StorageOptions.DataRoot)} must be configured.");
        }

        _paths = new UserPaths(options);
    }

    // ---- settings.json -----------------------------------------------------

    /// <inheritdoc />
    public async Task<UserSettings> GetSettingsAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.SettingsFile(iss, sub);
        if (!File.Exists(path))
        {
            return new UserSettings();
        }

        await using var stream = File.OpenRead(path);
        var settings = await JsonSerializer
            .DeserializeAsync<UserSettings>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return settings ?? new UserSettings();
    }

    /// <inheritdoc />
    public Task SaveSettingsAsync(
        string iss,
        string sub,
        UserSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return WriteJsonAsync(_paths.SettingsFile(iss, sub), settings, cancellationToken);
    }

    // ---- projects/{pid}/meta.json -----------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectMeta>> ListProjectsAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default)
    {
        var projectsDir = _paths.ProjectsDir(iss, sub);
        if (!Directory.Exists(projectsDir))
        {
            return [];
        }

        var results = new List<ProjectMeta>();
        foreach (var dir in Directory.EnumerateDirectories(projectsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metaFile = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaFile))
            {
                continue;
            }

            var meta = await ReadMetaAsync(metaFile, cancellationToken).ConfigureAwait(false);
            if (meta is not null)
            {
                results.Add(meta);
            }
        }

        return results
            .OrderBy(static m => m.CreatedAt)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ProjectMeta?> GetProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        var metaFile = _paths.ProjectMeta(iss, sub, pid);
        return File.Exists(metaFile)
            ? await ReadMetaAsync(metaFile, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <inheritdoc />
    public Task SaveProjectAsync(
        string iss,
        string sub,
        ProjectMeta meta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return WriteJsonAsync(_paths.ProjectMeta(iss, sub, meta.Id), meta, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // ProjectRoot validates the pid shape AND asserts the resolved path is under
        // the user root, so a value can never escape the caller's folder.
        var projectRoot = _paths.ProjectRoot(iss, sub, pid);
        if (!Directory.Exists(projectRoot))
        {
            return Task.FromResult(false);
        }

        // Close pooled SQLite handles first — open-per-use returns connections to the
        // pool, so a pooled handle to chat.db/rag.db would otherwise keep the unlinked
        // file alive (stale reads) after the directory is removed.
        SqliteConnection.ClearAllPools();
        Directory.Delete(projectRoot, recursive: true);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task EmptyProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var projectRoot = _paths.ProjectRoot(iss, sub, pid);
        if (!Directory.Exists(projectRoot))
        {
            return Task.CompletedTask;
        }

        // Drop pooled chat.db/rag.db handles so deleting the files doesn't leave stale
        // pooled connections that resurface old rows after the project is re-provisioned.
        SqliteConnection.ClearAllPools();

        foreach (var file in Directory.EnumerateFiles(projectRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(file);
        }

        foreach (var dir in Directory.EnumerateDirectories(projectRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(dir, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ---- account: rm -rf users/{key} (self) -------------------------------

    /// <inheritdoc />
    public Task<bool> DeleteUserAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var root = _paths.Root(iss, sub);
        return Task.FromResult(DeleteUnderUsers(root));
    }

    // ---- admin: scan users/*/meta.json ------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        var usersDir = _paths.UsersDir;
        if (!Directory.Exists(usersDir))
        {
            return [];
        }

        var results = new List<UserSummary>();
        foreach (var dir in Directory.EnumerateDirectories(usersDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var summary = await SummariseAsync(dir, cancellationToken).ConfigureAwait(false);
            if (summary is not null)
            {
                results.Add(summary);
            }
        }

        return results
            .OrderBy(static u => u.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<UserSummary?> GetUserAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var dir = ResolveUserDir(key);
        return dir is null || !Directory.Exists(dir)
            ? null
            : await SummariseAsync(dir, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> DeleteUserByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        cancellationToken.ThrowIfCancellationRequested();

        var dir = ResolveUserDir(key);
        return Task.FromResult(dir is not null && DeleteUnderUsers(dir));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Resolve a folder key to its directory, asserting it stays directly under
    /// <c>{DataRoot}/users</c> (security F6). Returns <see langword="null"/> when the
    /// key would escape that root, so no deletion is ever attempted out of tree.
    /// </summary>
    private string? ResolveUserDir(string key)
    {
        var usersRoot = Path.GetFullPath(_paths.UsersDir);
        var candidate = Path.GetFullPath(Path.Combine(usersRoot, key));

        var prefix = usersRoot.EndsWith(Path.DirectorySeparatorChar)
            ? usersRoot
            : usersRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        // A direct child only: a key must name a folder, never a nested path.
        return string.Equals(Path.GetDirectoryName(candidate), usersRoot, StringComparison.Ordinal)
            ? candidate
            : null;
    }

    /// <summary>
    /// <c>rm -rf</c> a directory after re-asserting it is under <c>{DataRoot}/users</c>.
    /// Returns <see langword="true"/> if it existed and was removed.
    /// </summary>
    private bool DeleteUnderUsers(string dir)
    {
        var usersRoot = Path.GetFullPath(_paths.UsersDir);
        var full = Path.GetFullPath(dir);

        var prefix = usersRoot.EndsWith(Path.DirectorySeparatorChar)
            ? usersRoot
            : usersRoot + Path.DirectorySeparatorChar;

        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Refusing to delete '{full}' — it is not under '{usersRoot}'.", nameof(dir));
        }

        if (!Directory.Exists(full))
        {
            return false;
        }

        // Close pooled SQLite handles under this folder before rm -rf.
        SqliteConnection.ClearAllPools();
        Directory.Delete(full, recursive: true);
        return true;
    }

    /// <summary>Summarise one user folder from its <c>meta.json</c> + on-disk footprint.</summary>
    private async Task<UserSummary?> SummariseAsync(string userDir, CancellationToken cancellationToken)
    {
        var metaFile = Path.Combine(userDir, "meta.json");
        if (!File.Exists(metaFile))
        {
            // A folder without its binding is not a valid user — skip it.
            return null;
        }

        UserMeta? meta;
        await using (var stream = File.OpenRead(metaFile))
        {
            meta = await JsonSerializer
                .DeserializeAsync<UserMeta>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        if (meta is null)
        {
            return null;
        }

        long sizeBytes = 0;
        var documentCount = 0;
        DateTimeOffset? lastActive = null;

        foreach (var file in Directory.EnumerateFiles(userDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new FileInfo(file);
            sizeBytes += info.Length;

            var written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (lastActive is null || written > lastActive)
            {
                lastActive = written;
            }

            // Document blobs live under any project's files/ directory.
            var parent = Path.GetFileName(Path.GetDirectoryName(file));
            if (string.Equals(parent, "files", StringComparison.Ordinal))
            {
                documentCount++;
            }
        }

        return new UserSummary
        {
            Key = Path.GetFileName(userDir.TrimEnd(Path.DirectorySeparatorChar)),
            Username = meta.Username,
            SizeBytes = sizeBytes,
            DocumentCount = documentCount,
            LastActive = lastActive,
        };
    }

    private static async Task<ProjectMeta?> ReadMetaAsync(string metaFile, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(metaFile);
        return await JsonSerializer
            .DeserializeAsync<ProjectMeta>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a temp sibling then move, so a crash never leaves a half-written
        // config file. CreatedAt is preserved by the caller (read/merge/write).
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }
}
