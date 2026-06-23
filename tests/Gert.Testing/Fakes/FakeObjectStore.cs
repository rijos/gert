using System.Collections.Concurrent;
using Gert.Storage;

namespace Gert.Testing.Fakes;

/// <summary>
/// An in-memory <see cref="IObjectStore"/> for tests - blobs held in a dictionary keyed by
/// scope + key, no disk. Enough for exercising the read/list paths (read_document) and standing
/// in where a turn needs an object store but the test never touches blobs.
/// </summary>
public sealed class FakeObjectStore : IObjectStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    private static string Prefix(ObjectScope scope) =>
        scope.IsProject ? $"{scope.UserKey}/{scope.Pid}/" : $"{scope.UserKey}/";

    private static string Id(ObjectScope scope, string key) => Prefix(scope) + key;

    /// <summary>Seed a blob directly (test arrange).</summary>
    public void Seed(ObjectScope scope, string key, byte[] content) => _blobs[Id(scope, key)] = content;

    public Task PutAsync(ObjectScope scope, string key, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        _blobs[Id(scope, key)] = buffer.ToArray();
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(ObjectScope scope, string key, CancellationToken cancellationToken = default)
    {
        if (!_blobs.TryGetValue(Id(scope, key), out var bytes))
        {
            throw new FileNotFoundException($"no blob at '{key}'");
        }

        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public Task<bool> ExistsAsync(ObjectScope scope, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_blobs.ContainsKey(Id(scope, key)));

    public Task<bool> DeleteAsync(ObjectScope scope, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_blobs.TryRemove(Id(scope, key), out _));

    public Task<int> DeletePrefixAsync(ObjectScope scope, string prefix, CancellationToken cancellationToken = default)
    {
        var full = Prefix(scope) + prefix;
        var removed = 0;
        foreach (var k in _blobs.Keys.Where(k => k.StartsWith(full, StringComparison.Ordinal)).ToList())
        {
            if (_blobs.TryRemove(k, out _))
            {
                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    public Task<bool> DeleteScopeAsync(ObjectScope scope, CancellationToken cancellationToken = default)
    {
        var full = Prefix(scope);
        var any = false;
        foreach (var k in _blobs.Keys.Where(k => k.StartsWith(full, StringComparison.Ordinal)).ToList())
        {
            any |= _blobs.TryRemove(k, out _);
        }

        return Task.FromResult(any);
    }

    public Task<IReadOnlyList<string>> ListAsync(ObjectScope scope, string prefix, CancellationToken cancellationToken = default)
    {
        var full = Prefix(scope) + prefix;
        IReadOnlyList<string> keys = _blobs.Keys
            .Where(k => k.StartsWith(full, StringComparison.Ordinal))
            .Select(k => k[Prefix(scope).Length..])
            .ToList();
        return Task.FromResult(keys);
    }

    public Task<IReadOnlyList<ObjectEntry>> ListEntriesAsync(ObjectScope scope, string prefix, CancellationToken cancellationToken = default)
    {
        var full = Prefix(scope) + prefix;
        IReadOnlyList<ObjectEntry> entries = _blobs
            .Where(kv => kv.Key.StartsWith(full, StringComparison.Ordinal))
            .Select(kv => new ObjectEntry(kv.Key[Prefix(scope).Length..], kv.Value.Length, DateTimeOffset.UnixEpoch))
            .ToList();
        return Task.FromResult(entries);
    }

    public Task<IReadOnlyList<string>> ListUserKeysAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> users = _blobs.Keys
            .Select(k => k.Split('/', 2)[0])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(users);
    }
}
