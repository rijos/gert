using Gert.Database;
using Gert.Model.Projects;
using Gert.Service.Storage;

namespace Gert.Service.Admin;

/// <summary>
/// Admin data-lifecycle surface (rest-api.md section admin; auth.md section matrix). Combines
/// the blob footprint from <see cref="IUserStore"/> with the username from each
/// user's <c>user.db</c> (<see cref="IUserDatabaseProvider"/>) to summarise users,
/// and <c>rm -rf</c>s a folder by key to delete one. It grants no cross-user data
/// read - it never opens another user's <c>chat.db</c>/<c>rag.db</c>. The
/// <c>{key}</c> is validated to <c>^[0-9a-f]{64}$</c> by the controller (security
/// F6) and the store asserts the resolved path stays under <c>{DataRoot}/users</c>
/// before any delete.
/// </summary>
public sealed class AdminService : IAdminService
{
    private readonly IUserStore _store;
    private readonly IUserDatabaseProvider _userDatabases;

    public AdminService(IUserStore store, IUserDatabaseProvider userDatabases)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _userDatabases = userDatabases ?? throw new ArgumentNullException(nameof(userDatabases));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        var footprints = await _store.ListUserFootprintsAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<UserSummary>(footprints.Count);
        foreach (var footprint in footprints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A folder with no username row (e.g. a partially provisioned user)
            // is still a real data folder: list it with a null username rather
            // than hiding it from the admin who may need to delete it.
            var username = await UsernameForAsync(footprint.Key, cancellationToken).ConfigureAwait(false);
            results.Add(ToSummary(footprint, username));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<UserSummary?> GetUserAsync(string key, CancellationToken cancellationToken = default)
    {
        var footprint = await _store.GetUserFootprintAsync(key, cancellationToken).ConfigureAwait(false);
        if (footprint is null)
        {
            return null;
        }

        // Same rule as ListUsersAsync: a missing username row does not hide the folder.
        var username = await UsernameForAsync(key, cancellationToken).ConfigureAwait(false);
        return ToSummary(footprint, username);
    }

    /// <inheritdoc />
    public Task<bool> DeleteUserAsync(string key, CancellationToken cancellationToken = default) =>
        _store.DeleteUserByKeyAsync(key, cancellationToken);

    private async Task<string?> UsernameForAsync(string key, CancellationToken cancellationToken)
    {
        await using var repo = await _userDatabases.OpenByKeyAsync(key, cancellationToken).ConfigureAwait(false);
        return await repo.GetUsernameAsync(cancellationToken).ConfigureAwait(false);
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
