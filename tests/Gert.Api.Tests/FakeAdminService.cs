using Gert.Model.Projects;
using Gert.Service.Admin;

namespace Gert.Api.Tests;

/// <summary>
/// A trivial in-memory <see cref="IAdminService"/> for the RBAC tests. The real
/// service is still a U7c stub (it throws), so this lets the admin-policy 200 path
/// be exercised end-to-end (the auth gate is what these tests assert, not the
/// folder-scan internals). It records the keys it was asked to act on so a test can
/// confirm the controller's F6 guard runs <b>before</b> the service is ever called.
/// </summary>
public sealed class FakeAdminService : IAdminService
{
    private readonly List<UserSummary> _users =
    [
        new UserSummary { Key = new string('a', 64), Username = "alice", DocumentCount = 2 },
    ];

    /// <summary>Keys passed to <see cref="GetUserAsync"/> / <see cref="DeleteUserAsync"/>.</summary>
    public List<string> SeenKeys { get; } = [];

    public Task<IReadOnlyList<UserSummary>> ListUsersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserSummary>>(_users);

    public Task<UserSummary?> GetUserAsync(string key, CancellationToken cancellationToken = default)
    {
        SeenKeys.Add(key);
        return Task.FromResult(_users.FirstOrDefault(u => u.Key == key));
    }

    public Task<bool> DeleteUserAsync(string key, CancellationToken cancellationToken = default)
    {
        SeenKeys.Add(key);
        var removed = _users.RemoveAll(u => u.Key == key) > 0;
        return Task.FromResult(removed);
    }
}
