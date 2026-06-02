using Gert.Model.Projects;

namespace Gert.Service.Admin;

/// <summary>
/// Admin data-lifecycle surface (rest-api.md § admin; auth.md § matrix). Confined
/// to scanning folder <c>meta.json</c> and deleting a folder — it grants no
/// cross-user data read. The <c>{key}</c> is validated to <c>^[0-9a-f]{64}$</c>
/// and asserted under the data root before any delete (security F6).
/// </summary>
public interface IAdminService
{
    /// <summary>List user folders by reading each <c>meta.json</c>.</summary>
    Task<IReadOnlyList<UserSummary>> ListUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>One user's folder summary by validated <paramref name="key"/>.</summary>
    Task<UserSummary?> GetUserAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>rm -rf /data/users/{key}</c> — removes all of that user's data. Does
    /// not touch the IdP account.
    /// </summary>
    Task<bool> DeleteUserAsync(string key, CancellationToken cancellationToken = default);
}
