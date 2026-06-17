using Gert.Database;
using Gert.Model.Projects;
using Gert.Service.Account;
using Gert.Storage;

namespace Gert.Service.Admin;

/// <summary>
/// Admin data-lifecycle surface (rest-api.md section admin; auth.md section matrix).
/// Combines the artifact footprint from <see cref="IObjectStore"/> (the user-folder
/// enumeration + a per-user blob scan) with the username from each user's
/// <c>user.db</c> (<see cref="IUserDatabaseProvider"/>) to summarise users, and
/// deletes one by orchestrating the independent stores. It grants no cross-user data read - it
/// never opens another user's <c>chat.db</c>/<c>rag.db</c>. The <c>{key}</c> is
/// validated to <c>^[0-9a-f]{64}$</c> by the controller (security F6) and re-asserted
/// here before any path is formed.
/// </summary>
public sealed class AdminService : IAdminService
{
    private readonly IObjectStore _objects;
    private readonly IUserDatabaseProvider _userDatabases;
    private readonly IUserDataEraser _eraser;

    public AdminService(
        IObjectStore objects,
        IUserDatabaseProvider userDatabases,
        IUserDataEraser eraser)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _userDatabases = userDatabases ?? throw new ArgumentNullException(nameof(userDatabases));
        _eraser = eraser ?? throw new ArgumentNullException(nameof(eraser));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        var keys = await _objects.ListUserKeysAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<UserSummary>(keys.Count);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var footprint = await FootprintAsync(key, cancellationToken).ConfigureAwait(false);
            if (footprint is null)
            {
                continue;
            }

            // A folder with no username row (e.g. a partially provisioned user) is still
            // a real data folder: list it with a null username rather than hiding it from
            // the admin who may need to delete it.
            var username = await UsernameForAsync(key, cancellationToken).ConfigureAwait(false);
            results.Add(ToSummary(footprint, username));
        }

        return results
            .OrderBy(static u => u.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<UserSummary?> GetUserAsync(string key, CancellationToken cancellationToken = default)
    {
        // An out-of-shape key addresses nothing (F6 defence-in-depth) - report absent.
        if (!IsWellShaped(key))
        {
            return null;
        }

        var footprint = await FootprintAsync(key, cancellationToken).ConfigureAwait(false);
        if (footprint is null)
        {
            return null;
        }

        // Same rule as ListUsersAsync: a missing username row does not hide the folder.
        var username = await UsernameForAsync(key, cancellationToken).ConfigureAwait(false);
        return ToSummary(footprint, username);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteUserAsync(string key, CancellationToken cancellationToken = default)
    {
        // The controller already shape-validated (F6); re-assert so an out-of-shape key
        // can never become a delete. Absent / malformed => false (idempotent).
        if (!IsWellShaped(key))
        {
            return false;
        }

        // Erase every store for this user through the journal-guarded eraser (db halves
        // before blobs), so a crash mid-delete is resumable rather than a half-erased folder.
        return await _eraser.EraseAsync(key, cancellationToken).ConfigureAwait(false);
    }

    // ---- helpers -----------------------------------------------------------

    private async Task<string?> UsernameForAsync(string key, CancellationToken cancellationToken)
    {
        await using var repo = await _userDatabases.OpenByKeyAsync(key, cancellationToken).ConfigureAwait(false);
        return await repo.GetUsernameAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Summarise one user scope's blob footprint, or <see langword="null"/> when the
    /// folder holds nothing at all - i.e. no such user.
    /// </summary>
    private async Task<UserFootprint?> FootprintAsync(string key, CancellationToken cancellationToken)
    {
        var scope = ObjectScope.FromUserKey(key);

        var entries = await _objects.ListEntriesAsync(scope, string.Empty, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return null;
        }

        long sizeBytes = 0;
        var documentCount = 0;
        DateTimeOffset? lastActive = null;

        foreach (var entry in entries)
        {
            sizeBytes += entry.Size;

            if (lastActive is null || entry.LastModified > lastActive)
            {
                lastActive = entry.LastModified;
            }

            if (IsDocumentKey(entry.Key))
            {
                documentCount++;
            }
        }

        return new UserFootprint
        {
            Key = key,
            Size = sizeBytes,
            DocumentCount = documentCount,
            LastActive = lastActive,
        };
    }

    /// <summary><c>projects/{pid}/files/{blob}</c> exactly - a stored upload.</summary>
    private static bool IsDocumentKey(string key)
    {
        var segments = key.Split('/');
        return segments is ["projects", _, "files", _];
    }

    /// <summary>True when <paramref name="key"/> passes the F6 shape guard (no throw).</summary>
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

    private static UserSummary ToSummary(UserFootprint footprint, string? username) => new()
    {
        Key = footprint.Key,
        Username = username,
        Size = footprint.Size,
        DocumentCount = footprint.DocumentCount,
        LastActive = footprint.LastActive,
    };
}
