using Gert.Database;
using Gert.Model.Projects;
using Gert.Service.Storage;

namespace Gert.Service.Provisioning;

/// <summary>
/// <see cref="IUserProvisioner"/> over <c>user.db</c>: opens the caller's user
/// database (which self-migrates), refreshes the username from the token when it has
/// changed, and seeds the <c>default</c> project row if absent. The per-project
/// <c>chat.db</c>/<c>rag.db</c> are left to materialise lazily on first open.
/// </summary>
public sealed class UserProvisioner : IUserProvisioner
{
    private const string DefaultProjectId = StorageKeys.DefaultProjectId;

    private readonly IUserDatabaseProvider _userDatabases;
    private readonly IUserContext _user;

    public UserProvisioner(IUserDatabaseProvider userDatabases, IUserContext user)
    {
        _userDatabases = userDatabases ?? throw new ArgumentNullException(nameof(userDatabases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public async Task EnsureCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        await using var repo = await _userDatabases
            .OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        // Refresh the username only when it actually changed — keep the steady-state
        // path read-only (no per-request write/WAL churn).
        var current = await repo.GetUsernameAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current, _user.Username, StringComparison.Ordinal))
        {
            await repo.SetUsernameAsync(_user.Username, cancellationToken).ConfigureAwait(false);
        }

        if (await repo.GetProjectAsync(DefaultProjectId, cancellationToken).ConfigureAwait(false) is null)
        {
            var now = DateTimeOffset.UtcNow;
            await repo.SaveProjectAsync(
                new ProjectMeta
                {
                    Id = DefaultProjectId,
                    Name = "Default",
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}
