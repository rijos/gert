using Microsoft.Extensions.Options;

namespace Gert.Storage.Local;

/// <summary>
/// Local-filesystem <see cref="IDeletionJournal"/>: one empty marker file per owed deletion
/// under <c>{DataRoot}/.pending-deletions/{key}</c> - a sibling of <c>users/</c>, so it
/// survives the user-tree wipe (the eraser clears it only as the last step) and never shows
/// up in the admin user scan. A marker's mere presence is the signal. Keys are
/// shape-validated (security F6) before any path is formed, so a key can only ever name one
/// marker - never a prefix or a path.
/// </summary>
public sealed class LocalDeletionJournal : IDeletionJournal
{
    private const string DirName = ".pending-deletions";

    private readonly string _dir;

    /// <summary>Resolve the journal directory under the shared <see cref="StorageOptions.DataRoot"/>.</summary>
    public LocalDeletionJournal(IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dataRoot = options.Value.DataRoot;
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new InvalidOperationException(
                $"{nameof(StorageOptions)}.{nameof(StorageOptions.DataRoot)} must be configured.");
        }

        _dir = Path.Combine(dataRoot, DirName);
    }

    /// <inheritdoc />
    public Task MarkPendingAsync(string userKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StorageKeys.ValidateUserKey(userKey);

        Directory.CreateDirectory(_dir);
        // The marker's presence is the whole signal; an empty file is atomic enough
        // (it either exists or it doesn't). Create-or-truncate keeps it idempotent.
        using (File.Create(MarkerPath(userKey)))
        {
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(string userKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StorageKeys.ValidateUserKey(userKey);

        var path = MarkerPath(userKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> IsPendingAsync(string userKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StorageKeys.ValidateUserKey(userKey);

        return Task.FromResult(File.Exists(MarkerPath(userKey)));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(_dir))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var keys = Directory.EnumerateFiles(_dir)
            .Select(static p => Path.GetFileName(p))
            .Where(IsWellShaped)
            .OrderBy(static k => k, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    private string MarkerPath(string userKey) => Path.Combine(_dir, userKey);

    /// <summary>True when <paramref name="key"/> is a valid folder key (skip stray non-marker files).</summary>
    private static bool IsWellShaped(string key)
    {
        try
        {
            StorageKeys.ValidateUserKey(key);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
