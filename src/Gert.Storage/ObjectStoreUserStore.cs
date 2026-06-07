using Gert.Model.Projects;
using Gert.Service.Storage;

namespace Gert.Storage;

/// <summary>
/// <see cref="IUserStore"/> over <see cref="IObjectStore"/> — fully storage-backend
/// agnostic. Now that the structured config lives in <c>user.db</c>, this is just
/// the coarse blob lifecycle: scope deletes (the <c>rm -rf</c> of principle #5,
/// which also takes the scope's <c>chat.db</c>/<c>rag.db</c> with it) and the admin
/// footprint scan (object listing). Swap the <see cref="IObjectStore"/> backend
/// (local → S3 → Azure Blob) and this class doesn't change.
///
/// <para>
/// Identity is threaded exactly as the other seams: <c>(iss, sub)</c> come only from
/// the validated token and are hashed into the <see cref="ObjectScope"/>; admin
/// keys are shape-validated by the scope factories (security F6), so an operation
/// can never select another user's objects.
/// </para>
/// </summary>
public sealed class ObjectStoreUserStore : IUserStore
{
    private readonly IObjectStore _objects;

    /// <summary>Create the store over the configured <see cref="IObjectStore"/> backend.</summary>
    public ObjectStoreUserStore(IObjectStore objects)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
    }

    // ---- project blob lifecycle -------------------------------------------

    /// <inheritdoc />
    public Task<bool> DeleteProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default) =>
        _objects.DeleteScopeAsync(ObjectScope.Project(iss, sub, pid), cancellationToken);

    /// <inheritdoc />
    public Task EmptyProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default) =>
        // Empty prefix clears every object in the project (incl. chat.db/rag.db);
        // DeletePrefix keeps the scope root itself — the default project is emptied,
        // never removed (configuration.md § 5). The databases re-materialise on the
        // next open.
        _objects.DeletePrefixAsync(ObjectScope.Project(iss, sub, pid), string.Empty, cancellationToken);

    // ---- account (the user scope) ------------------------------------------

    /// <inheritdoc />
    public Task<bool> DeleteUserAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default) =>
        _objects.DeleteScopeAsync(ObjectScope.User(iss, sub), cancellationToken);

    /// <inheritdoc />
    public async Task<bool> DeleteUserByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        // The controller already shape-validated (F6); re-assert here so an
        // out-of-shape key can never become a delete. Absent => false (idempotent).
        try
        {
            StorageKeys.ValidateUserKey(key);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return await _objects.DeleteScopeAsync(ObjectScope.FromUserKey(key), cancellationToken)
            .ConfigureAwait(false);
    }

    // ---- admin (scan user scopes) ------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserFootprint>> ListUserFootprintsAsync(CancellationToken cancellationToken = default)
    {
        var keys = await _objects.ListUserKeysAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<UserFootprint>();
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var footprint = await FootprintAsync(key, cancellationToken).ConfigureAwait(false);
            if (footprint is not null)
            {
                results.Add(footprint);
            }
        }

        return results
            .OrderBy(static f => f.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<UserFootprint?> GetUserFootprintAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        // An out-of-shape key addresses nothing (F6) — report absent.
        try
        {
            StorageKeys.ValidateUserKey(key);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return await FootprintAsync(key, cancellationToken).ConfigureAwait(false);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Summarise one user scope's blob footprint, or <see langword="null"/> when the
    /// folder holds nothing at all (no <c>user.db</c>, no blobs) — i.e. no such user.
    /// </summary>
    private async Task<UserFootprint?> FootprintAsync(string key, CancellationToken cancellationToken)
    {
        var scope = ObjectScope.FromUserKey(key);

        var entries = await _objects.ListEntriesAsync(scope, string.Empty, cancellationToken)
            .ConfigureAwait(false);
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

            // Document blobs are the direct children of a project's files/ segment.
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

    /// <summary><c>projects/{pid}/files/{blob}</c> exactly — a stored upload.</summary>
    private static bool IsDocumentKey(string key)
    {
        var segments = key.Split('/');
        return segments is ["projects", _, "files", _];
    }
}
