using Gert.Service.Storage;

namespace Gert.Database.Sqlite;

/// <summary>
/// Filesystem <see cref="IObjectStore"/> — the local-persistence adapter that
/// owns <see cref="UserPaths"/>/<c>DataRoot</c>. Blobs live under
/// <c>projects/{pid}/files/</c> (<see cref="UserPaths.FilesDir"/>); a key is a
/// project-relative path joined under that <c>files/</c> root.
///
/// <para>
/// Identity is handled exactly as the database seam: <c>(iss, sub)</c> are
/// token-derived and <c>pid</c> is validated by <see cref="UserPaths"/>, so the
/// scope can only resolve under the caller's own project folder. On top of that,
/// every key is normalised and asserted to stay <b>under</b> the resolved
/// <c>files/</c> root (<c>..</c> and rooted/absolute keys are rejected) — a key can
/// never escape the project dir.
/// </para>
///
/// <para>
/// A future object-storage backend is a drop-in:
/// </para>
/// <code>// S3: new IObjectStore impl, one DI registration</code>
/// </summary>
public sealed class LocalObjectStore(UserPaths paths) : IObjectStore
{
    private readonly UserPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

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

        await using var file = File.Create(path);
        await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
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
            throw new FileNotFoundException($"No blob at key '{key}'.", path);
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

        var root = FilesRoot(scope);
        var matched = EnumerateUnder(root, prefix).ToList();
        foreach (var path in matched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }

        return Task.FromResult(matched.Count);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(
        ObjectScope scope,
        string prefix,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var root = FilesRoot(scope);
        var keys = EnumerateUnder(root, prefix)
            .Select(path => ToKey(root, path))
            .OrderBy(static k => k, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    // ---- path resolution + traversal guard --------------------------------

    /// <summary>The scope's <c>files/</c> root (pid validated by <see cref="UserPaths"/>).</summary>
    private string FilesRoot(ObjectScope scope) => _paths.FilesDir(scope.Iss, scope.Sub, scope.Pid);

    /// <summary>
    /// Resolve <paramref name="key"/> to an absolute path under the scope's
    /// <c>files/</c> root, rejecting any key that escapes it (<c>..</c> segments or
    /// rooted/absolute paths). Defence-in-depth on top of the validated scope.
    /// </summary>
    private string ResolveKey(ObjectScope scope, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (Path.IsPathRooted(key))
        {
            throw new ArgumentException($"Object key '{key}' must be relative.", nameof(key));
        }

        var root = Path.GetFullPath(FilesRoot(scope));
        var combined = Path.GetFullPath(Path.Combine(root, key));

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Object key '{key}' escapes the project files root.", nameof(key));
        }

        return combined;
    }

    /// <summary>
    /// Enumerate existing files under <paramref name="root"/> whose project-relative
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
}
