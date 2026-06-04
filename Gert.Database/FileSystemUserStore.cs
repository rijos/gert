using System.Globalization;
using System.Text.Json;
using Gert.Model.Projects;
using Gert.Service.Database;
using Gert.Service.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Database;

/// <summary>
/// Filesystem <see cref="IUserStore"/> — the local-persistence adapter that owns
/// <see cref="UserPaths"/> / <c>DataRoot</c>. It reads/writes the config files
/// (the user-root <c>meta.json</c> sidecar, <c>settings.json</c>,
/// <c>projects/{pid}/meta.json</c>) with direct file I/O — these are config, not
/// user blobs — and performs the coarse directory lifecycle (<c>rm -rf</c> a
/// project/user folder, enumerate users) as plain filesystem ops. Database-agnostic:
/// destructive directory ops release engine-held file handles through the
/// <see cref="IDatabaseHandleReleaser"/> port rather than any concrete driver.
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
    // One serializer for every config file so meta.json/settings.json round-trip
    // byte-for-byte regardless of which component last wrote them.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly StorageOptions _options;
    private readonly UserPaths _paths;
    private readonly ILogger<FileSystemUserStore> _logger;
    private readonly IDatabaseHandleReleaser _dbHandles;

    /// <summary>Create the store with bound <see cref="StorageOptions"/>.</summary>
    public FileSystemUserStore(
        IOptions<StorageOptions> options,
        IDatabaseHandleReleaser dbHandles,
        ILogger<FileSystemUserStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dbHandles = dbHandles ?? throw new ArgumentNullException(nameof(dbHandles));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.DataRoot))
        {
            throw new InvalidOperationException(
                $"{nameof(StorageOptions)}.{nameof(StorageOptions.DataRoot)} must be configured.");
        }

        _paths = new UserPaths(options);
    }

    // ---- provisioning (user root + project skeleton on disk) ---------------

    /// <inheritdoc />
    public async Task EnsureUserFilesAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.Root(iss, sub));

        // meta.json is a descriptive sidecar, not a gate: the identity is trusted once
        // the JWT validates (the folder key is derived from it and nothing else). It
        // exists so the admin scan can map the opaque hash folder to a username, and
        // schema_version anchors future layout migrations. Missing or unreadable
        // (e.g. truncated by an interrupted write) -> rewrite from the token; a
        // healthy file is left alone so created_at survives.
        var metaFile = _paths.MetaFile(iss, sub);
        var existing = await ReadJsonOrNullAsync<UserMeta>(metaFile, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            var meta = new UserMeta
            {
                Iss = iss,
                Sub = sub,
                Username = sub, // username is refreshed from the token elsewhere; sub is the safe default.
                CreatedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                SchemaVersion = UserMeta.CurrentSchemaVersion,
            };
            await WriteJsonAsync(metaFile, meta, cancellationToken).ConfigureAwait(false);
        }

        // settings.json (defaults) — written only when absent so user edits survive.
        var settingsFile = _paths.SettingsFile(iss, sub);
        if (!File.Exists(settingsFile))
        {
            await WriteJsonAsync(settingsFile, new UserSettings(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task EnsureProjectFilesAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        // ProjectRoot validates the pid shape AND asserts the resolved path stays
        // under the user root.
        Directory.CreateDirectory(_paths.ProjectRoot(iss, sub, pid));
        Directory.CreateDirectory(_paths.FilesDir(iss, sub, pid));
        Directory.CreateDirectory(_paths.MemoryDir(iss, sub, pid));

        var metaFile = _paths.ProjectMeta(iss, sub, pid);
        if (!File.Exists(metaFile))
        {
            var now = DateTimeOffset.UtcNow;
            var meta = new ProjectMeta
            {
                Id = pid,
                Name = pid == UserPaths.DefaultProjectId ? "Default" : pid,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await WriteJsonAsync(metaFile, meta, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- settings.json -----------------------------------------------------

    /// <inheritdoc />
    public async Task<UserSettings> GetSettingsAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default)
    {
        // A missing OR empty/corrupt settings.json falls back to defaults rather
        // than 500-ing the read — see ReadJsonOrNullAsync.
        var settings = await ReadJsonOrNullAsync<UserSettings>(
            _paths.SettingsFile(iss, sub), cancellationToken).ConfigureAwait(false);
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

            // A missing/empty/corrupt meta.json is skipped (not fatal): one bad
            // project folder must not sink the whole list.
            var metaFile = Path.Combine(dir, "meta.json");
            var meta = await ReadJsonOrNullAsync<ProjectMeta>(metaFile, cancellationToken)
                .ConfigureAwait(false);
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
        return await ReadJsonOrNullAsync<ProjectMeta>(metaFile, cancellationToken)
            .ConfigureAwait(false);
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
        _dbHandles.ReleaseAll();
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
        _dbHandles.ReleaseAll();

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
        _dbHandles.ReleaseAll();
        Directory.Delete(full, recursive: true);
        return true;
    }

    /// <summary>Summarise one user folder from its <c>meta.json</c> + on-disk footprint.</summary>
    private async Task<UserSummary?> SummariseAsync(string userDir, CancellationToken cancellationToken)
    {
        // A folder whose meta.json is missing, empty, or corrupt is not a usable
        // user binding — skip it (warned, not thrown) so the admin list survives one
        // bad folder instead of 500-ing the whole enumeration.
        var metaFile = Path.Combine(userDir, "meta.json");
        var meta = await ReadJsonOrNullAsync<UserMeta>(metaFile, cancellationToken)
            .ConfigureAwait(false);
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
            Size = sizeBytes,
            DocumentCount = documentCount,
            LastActive = lastActive,
        };
    }

    /// <summary>
    /// Read and deserialize a small JSON config file (<c>settings.json</c>,
    /// <c>meta.json</c>), tolerating a missing, empty, or corrupt file by returning
    /// <see langword="null"/> instead of throwing. A truncated config — e.g. a 0-byte
    /// file left by a process killed mid-write before <see cref="WriteJsonAsync"/>'s
    /// atomic rename, or an externally-interrupted write — must not 500 a read
    /// endpoint (it would otherwise sink whole-list reads like the admin user scan).
    /// A missing file is the normal "not provisioned yet" case and is silent; an
    /// empty/unparseable present file is real, recoverable corruption and is logged.
    /// </summary>
    private async Task<T?> ReadJsonOrNullAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length == 0)
            {
                _logger.LogWarning("Config file {Path} is empty; treating as absent.", path);
                return null;
            }

            return await JsonSerializer
                .DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Config file {Path} is not valid JSON; treating as absent.", path);
            return null;
        }
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
