using Gert.Database;
using Gert.Service.Storage;
using Microsoft.Extensions.Options;

namespace Gert.Storage;

/// <summary>
/// Local-filesystem <see cref="IObjectStore"/> — the default storage backend. A
/// scope resolves under <c>{DataRoot}/users/{key}</c> (user root) or
/// <c>…/projects/{pid}</c> (project root); a key is a scope-relative path joined
/// under that root. PUTs are <b>atomic</b> (temp sibling + rename) per the port
/// contract, so a reader never observes a partial object.
///
/// <para>
/// Identity is handled exactly as the database seam: the scope carries only the
/// validated opaque user key and a validated <c>pid</c>, and every key is
/// normalised and asserted to stay <b>under</b> the resolved scope root
/// (<c>..</c> and rooted/absolute keys are rejected) — a key can never escape it.
/// Destructive whole-tree deletes first release engine-held database file handles
/// through <see cref="IDatabaseHandleReleaser"/> (locally, <c>chat.db</c>/<c>rag.db</c>
/// live inside the same tree), so a delete never operates on unlinked-but-open files.
/// </para>
/// </summary>
public sealed class LocalObjectStore : IObjectStore
{
    private readonly StorageOptions _options;
    private readonly IDatabaseHandleReleaser _dbHandles;

    /// <summary>Create the backend over the configured <see cref="StorageOptions.DataRoot"/>.</summary>
    public LocalObjectStore(IOptions<StorageOptions> options, IDatabaseHandleReleaser dbHandles)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dbHandles = dbHandles ?? throw new ArgumentNullException(nameof(dbHandles));
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.DataRoot))
        {
            throw new InvalidOperationException(
                $"{nameof(StorageOptions)}.{nameof(StorageOptions.DataRoot)} must be configured.");
        }
    }

    private string UsersDir => Path.Combine(_options.DataRoot, "users");

    /// <inheritdoc />
    public async Task PutAsync(
        ObjectScope scope,
        string key,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = ResolveKey(scope, key);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Atomic per the port contract: stage to a temp sibling, then rename. A
        // kill/cancel mid-copy never leaves a truncated object at the real key.
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = File.Create(temp))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(
        ObjectScope scope,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = ResolveKey(scope, key);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No object at key '{key}'.", path);
        }

        Stream stream = File.OpenRead(path);
        return Task.FromResult(stream);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(
        ObjectScope scope,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(ResolveKey(scope, key)));
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(
        ObjectScope scope,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = ResolveKey(scope, key);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<int> DeletePrefixAsync(
        ObjectScope scope,
        string prefix,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A user-root sweep or a whole-project sweep can hit chat.db/rag.db files
        // living in the same local tree — drop pooled handles first.
        if (!scope.IsProject || prefix.Length == 0)
        {
            _dbHandles.ReleaseAll();
        }

        var root = ScopeRoot(scope);
        var matched = EnumerateUnder(root, prefix).ToList();
        foreach (var path in matched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }

        // Keep the scope root itself (the empty-project contract: emptied, never
        // removed) but prune now-empty subdirectories beneath it.
        PruneEmptyDirectories(root, keepRoot: true);
        return Task.FromResult(matched.Count);
    }

    /// <inheritdoc />
    public Task<bool> DeleteScopeAsync(
        ObjectScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var root = ScopeRoot(scope);
        if (!Directory.Exists(root))
        {
            return Task.FromResult(false);
        }

        // The tree contains chat.db/rag.db locally — drop pooled handles before rm -rf.
        _dbHandles.ReleaseAll();
        Directory.Delete(root, recursive: true);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(
        ObjectScope scope,
        string prefix,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var root = ScopeRoot(scope);
        var keys = EnumerateUnder(root, prefix)
            .Select(path => ToKey(root, path))
            .OrderBy(static k => k, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ObjectEntry>> ListEntriesAsync(
        ObjectScope scope,
        string prefix,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var root = ScopeRoot(scope);
        var entries = EnumerateUnder(root, prefix)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new ObjectEntry(
                    ToKey(root, path),
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            })
            .OrderBy(static e => e.Key, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<ObjectEntry>>(entries);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListUserKeysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(UsersDir))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var keys = Directory.EnumerateDirectories(UsersDir)
            .Select(static dir => Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)))
            .OrderBy(static k => k, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    // ---- path resolution + traversal guard --------------------------------

    /// <summary>The scope's local root: <c>users/{key}</c> or <c>users/{key}/projects/{pid}</c>.</summary>
    private string ScopeRoot(ObjectScope scope)
    {
        // The scope factories validated the key/pid shapes; assert anyway so a
        // default(ObjectScope) can never address the users root itself.
        StorageKeys.ValidateUserKey(scope.UserKey);

        var userRoot = Path.Combine(UsersDir, scope.UserKey);
        if (scope.Pid is not { } pid)
        {
            return userRoot;
        }

        StorageKeys.ValidatePid(pid);
        return Path.Combine(userRoot, "projects", pid);
    }

    /// <summary>
    /// Resolve <paramref name="key"/> to an absolute path under the scope root,
    /// rejecting any key that escapes it (<c>..</c> segments or rooted/absolute
    /// paths). Defence-in-depth on top of the validated scope.
    /// </summary>
    private string ResolveKey(ObjectScope scope, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (Path.IsPathRooted(key))
        {
            throw new ArgumentException($"Object key '{key}' must be relative.", nameof(key));
        }

        var root = Path.GetFullPath(ScopeRoot(scope));
        var combined = Path.GetFullPath(Path.Combine(root, key));

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Object key '{key}' escapes the scope root.", nameof(key));
        }

        return combined;
    }

    /// <summary>
    /// Enumerate existing files under <paramref name="root"/> whose scope-relative
    /// key starts with <paramref name="prefix"/>. The prefix is itself guarded so it
    /// can never widen the search outside the root.
    /// </summary>
    private static IEnumerable<string> EnumerateUnder(string root, string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        if (!Directory.Exists(root))
        {
            return [];
        }

        var fullRoot = Path.GetFullPath(root);
        var normalisedPrefix = prefix.Replace('\\', '/');

        return Directory
            .EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
            .Where(path => ToKey(fullRoot, path).StartsWith(normalisedPrefix, StringComparison.Ordinal));
    }

    /// <summary>Convert an absolute path under <paramref name="root"/> to a <c>/</c>-separated key.</summary>
    private static string ToKey(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Replace('\\', '/');
    }

    /// <summary>
    /// Remove now-empty directories bottom-up under <paramref name="root"/> —
    /// per-object deletes are the unit of the port, so emptied local folders are
    /// just artifacts (an object store has no directories).
    /// </summary>
    private static void PruneEmptyDirectories(string root, bool keepRoot)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory
            .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(static d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }

        if (!keepRoot && !Directory.EnumerateFileSystemEntries(root).Any())
        {
            Directory.Delete(root);
        }
    }
}
