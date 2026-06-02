using Gert.Model.Projects;

namespace Gert.Service.Admin;

/// <summary>
/// Stub. The admin surface scans each user folder's <c>meta.json</c> and can
/// delete a folder by validated key. The data-root seam isn't wired into the
/// service layer yet, so this is deferred.
/// <para>
/// // TODO U7c: implement against the host-provided data root — list/get read
/// each <c>meta.json</c>; delete validates <c>^[0-9a-f]{64}$</c> + asserts the
/// path is under the data root before <c>rm -rf</c> (security F6).
/// </para>
/// Present so <see cref="GertServices"/> + DI compile for the M1 gate.
/// </summary>
public sealed class AdminService : IAdminService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<UserSummary>> ListUsersAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");

    /// <inheritdoc />
    public Task<UserSummary?> GetUserAsync(string key, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");

    /// <inheritdoc />
    public Task<bool> DeleteUserAsync(string key, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");
}
