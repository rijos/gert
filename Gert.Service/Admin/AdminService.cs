using Gert.Model.Projects;
using Gert.Service.Storage;

namespace Gert.Service.Admin;

/// <summary>
/// Admin data-lifecycle surface (rest-api.md § admin; auth.md § matrix) over
/// <see cref="IUserStore"/> — list/get scan each folder's <c>meta.json</c>, delete
/// <c>rm -rf</c>s a folder by key. It grants no cross-user data read (it never opens
/// another user's <c>chat.db</c>/<c>rag.db</c>). The <c>{key}</c> is validated to
/// <c>^[0-9a-f]{64}$</c> by the controller (security F6) and the store asserts the
/// resolved path stays under <c>{DataRoot}/users</c> before any delete.
/// </summary>
public sealed class AdminService : IAdminService
{
    private readonly IUserStore _store;

    public AdminService(IUserStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public Task<IReadOnlyList<UserSummary>> ListUsersAsync(CancellationToken cancellationToken = default) =>
        _store.ListUsersAsync(cancellationToken);

    /// <inheritdoc />
    public Task<UserSummary?> GetUserAsync(string key, CancellationToken cancellationToken = default) =>
        _store.GetUserAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteUserAsync(string key, CancellationToken cancellationToken = default) =>
        _store.DeleteUserByKeyAsync(key, cancellationToken);
}
