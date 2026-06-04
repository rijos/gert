using System.Globalization;
using System.Text.Json;
using Gert.Model.Projects;
using Gert.Service.Storage;
using Microsoft.Extensions.Logging;

namespace Gert.Storage;

/// <summary>
/// <see cref="IUserStore"/> over <see cref="IObjectStore"/> — fully storage-backend
/// agnostic. The config files (the user-root <c>meta.json</c> sidecar,
/// <c>settings.json</c>, <c>projects/{pid}/meta.json</c>) are just small JSON
/// objects put/got through the object seam; the coarse lifecycle is scope deletes
/// (<c>DeleteScopeAsync</c> = the <c>rm -rf</c> of principle #5) and the admin scan
/// is object listing. Swap the <see cref="IObjectStore"/> backend (local → S3 →
/// Azure Blob) and this class doesn't change.
///
/// <para>
/// Identity is threaded exactly as the other seams: <c>(iss, sub)</c> come only
/// from the validated token and are hashed into the <see cref="ObjectScope"/>;
/// <c>pid</c> and admin-supplied user keys are shape-validated by the scope
/// factories (security F6), so an operation can never select another user's or
/// project's objects.
/// </para>
/// </summary>
public sealed class ObjectStoreUserStore : IUserStore
{
    // One serializer for every config file so meta.json/settings.json round-trip
    // byte-for-byte regardless of which component last wrote them.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private const string UserMetaKey = "meta.json";
    private const string SettingsKey = "settings.json";
    private const string ProjectMetaKey = "meta.json";

    private readonly IObjectStore _objects;
    private readonly ILogger<ObjectStoreUserStore> _logger;

    /// <summary>Create the store over the configured <see cref="IObjectStore"/> backend.</summary>
    public ObjectStoreUserStore(IObjectStore objects, ILogger<ObjectStoreUserStore> logger)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ---- provisioning (user root + project sidecars) ------------------------

    /// <inheritdoc />
    public async Task EnsureUserFilesAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default)
    {
        var scope = ObjectScope.User(iss, sub);

        // meta.json is a descriptive sidecar, not a gate: the identity is trusted once
        // the JWT validates (the scope key is derived from it and nothing else). It
        // exists so the admin scan can map the opaque key to a username, and
        // schema_version anchors future layout migrations. Missing or unreadable
        // (e.g. truncated by an interrupted write) -> rewrite from the token; a
        // healthy object is left alone so created_at survives.
        var existing = await ReadJsonOrNullAsync<UserMeta>(scope, UserMetaKey, cancellationToken)
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
            await WriteJsonAsync(scope, UserMetaKey, meta, cancellationToken).ConfigureAwait(false);
        }

        // settings.json (defaults) — written only when absent so user edits survive.
        if (!await _objects.ExistsAsync(scope, SettingsKey, cancellationToken).ConfigureAwait(false))
        {
            await WriteJsonAsync(scope, SettingsKey, new UserSettings(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task EnsureProjectFilesAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default)
    {
        var scope = ObjectScope.Project(iss, sub, pid);
        if (await _objects.ExistsAsync(scope, ProjectMetaKey, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var meta = new ProjectMeta
        {
            Id = pid,
            Name = pid == StorageKeys.DefaultProjectId ? "Default" : pid,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await WriteJsonAsync(scope, ProjectMetaKey, meta, cancellationToken).ConfigureAwait(false);
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
            ObjectScope.User(iss, sub), SettingsKey, cancellationToken).ConfigureAwait(false);
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
        return WriteJsonAsync(ObjectScope.User(iss, sub), SettingsKey, settings, cancellationToken);
    }

    // ---- projects/{pid}/meta.json -----------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectMeta>> ListProjectsAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default)
    {
        var scope = ObjectScope.User(iss, sub);
        var keys = await _objects.ListAsync(scope, "projects/", cancellationToken).ConfigureAwait(false);

        var results = new List<ProjectMeta>();
        foreach (var key in keys.Where(static k => IsProjectMetaKey(k)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A missing/empty/corrupt meta.json is skipped (not fatal): one bad
            // project must not sink the whole list.
            var meta = await ReadJsonOrNullAsync<ProjectMeta>(scope, key, cancellationToken)
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
    public Task<ProjectMeta?> GetProjectAsync(
        string iss,
        string sub,
        string pid,
        CancellationToken cancellationToken = default) =>
        ReadJsonOrNullAsync<ProjectMeta>(ObjectScope.Project(iss, sub, pid), ProjectMetaKey, cancellationToken);

    /// <inheritdoc />
    public Task SaveProjectAsync(
        string iss,
        string sub,
        ProjectMeta meta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return WriteJsonAsync(ObjectScope.Project(iss, sub, meta.Id), ProjectMetaKey, meta, cancellationToken);
    }

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
        // Empty prefix clears every object in the project; DeletePrefix keeps the
        // scope root itself — the default project is emptied, never removed
        // (configuration.md § 5).
        _objects.DeletePrefixAsync(ObjectScope.Project(iss, sub, pid), string.Empty, cancellationToken);

    // ---- account (the user scope) ------------------------------------------

    /// <inheritdoc />
    public Task<bool> DeleteUserAsync(
        string iss,
        string sub,
        CancellationToken cancellationToken = default) =>
        _objects.DeleteScopeAsync(ObjectScope.User(iss, sub), cancellationToken);

    // ---- admin (scan user scopes) ------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        var keys = await _objects.ListUserKeysAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<UserSummary>();
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var summary = await SummariseAsync(key, cancellationToken).ConfigureAwait(false);
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

        // An out-of-shape key addresses nothing (F6) — report absent.
        try
        {
            StorageKeys.ValidateUserKey(key);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return await SummariseAsync(key, cancellationToken).ConfigureAwait(false);
    }

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

    // ---- helpers -----------------------------------------------------------

    /// <summary>Summarise one user scope from its <c>meta.json</c> + object footprint.</summary>
    private async Task<UserSummary?> SummariseAsync(string key, CancellationToken cancellationToken)
    {
        var scope = ObjectScope.FromUserKey(key);

        // A scope whose meta.json is missing, empty, or corrupt is not a usable
        // user — skip it (logged by ReadJsonOrNullAsync) rather than failing the scan.
        var meta = await ReadJsonOrNullAsync<UserMeta>(scope, UserMetaKey, cancellationToken)
            .ConfigureAwait(false);
        if (meta is null)
        {
            return null;
        }

        var entries = await _objects.ListEntriesAsync(scope, string.Empty, cancellationToken)
            .ConfigureAwait(false);

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

        return new UserSummary
        {
            Key = key,
            Username = meta.Username,
            Size = sizeBytes,
            DocumentCount = documentCount,
            LastActive = lastActive,
        };
    }

    /// <summary><c>projects/{pid}/meta.json</c> exactly (no deeper nesting).</summary>
    private static bool IsProjectMetaKey(string key)
    {
        var segments = key.Split('/');
        return segments is ["projects", _, "meta.json"];
    }

    /// <summary><c>projects/{pid}/files/{blob}</c> exactly — a stored upload.</summary>
    private static bool IsDocumentKey(string key)
    {
        var segments = key.Split('/');
        return segments is ["projects", _, "files", _];
    }

    /// <summary>
    /// Read and deserialize a small JSON config object, tolerating a missing,
    /// empty, or corrupt one by returning <see langword="null"/> instead of
    /// throwing. A truncated config — e.g. one left by an interrupted writer
    /// outside the atomic-PUT path — must not 500 a read endpoint (it would
    /// otherwise sink whole-list reads like the admin scan). A missing object is
    /// the normal "not provisioned yet" case and is silent; an unparseable present
    /// one is real, recoverable corruption and is logged.
    /// </summary>
    private async Task<T?> ReadJsonOrNullAsync<T>(
        ObjectScope scope,
        string key,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!await _objects.ExistsAsync(scope, key, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            await using var stream = await _objects.OpenReadAsync(scope, key, cancellationToken)
                .ConfigureAwait(false);
            return await JsonSerializer
                .DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // Raced with a delete between Exists and OpenRead — absent.
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Config object {Key} is not valid JSON; treating as absent.", key);
            return null;
        }
    }

    /// <summary>Serialize and PUT a config object (atomic per the <see cref="IObjectStore"/> contract).</summary>
    private async Task WriteJsonAsync<T>(
        ObjectScope scope,
        string key,
        T value,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(buffer, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        await _objects.PutAsync(scope, key, buffer, cancellationToken).ConfigureAwait(false);
    }
}
