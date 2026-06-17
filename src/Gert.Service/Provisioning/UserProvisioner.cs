using Gert.Database;
using Gert.Model.Projects;
using Gert.Service.Account;
using Gert.Storage;

namespace Gert.Service.Provisioning;

/// <summary>
/// <see cref="IUserProvisioner"/> over <c>user.db</c>: opens the caller's user
/// database (which self-migrates), refreshes the username from the token when it has
/// changed, and seeds the <c>default</c> project row if absent. The per-project
/// <c>chat.db</c>/<c>rag.db</c> are left to materialise lazily on first open.
///
/// <para>
/// It also closes the self-service-delete race: if a prior account deletion for this user
/// was interrupted (a mark is still owed in the <see cref="IDeletionJournal"/>), it finishes
/// erasing the residue via <see cref="IUserDataEraser"/> <b>before</b> provisioning, so a
/// returning user never half-resurrects on top of half-deleted data.
/// </para>
/// </summary>
public sealed class UserProvisioner : IUserProvisioner
{
    private const string DefaultProjectId = StorageKeys.DefaultProjectId;

    private readonly IUserDatabaseProvider _userDatabases;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;
    private readonly IDeletionJournal _journal;
    private readonly IUserDataEraser _eraser;

    public UserProvisioner(
        IUserDatabaseProvider userDatabases,
        IUserContext user,
        TimeProvider time,
        IDeletionJournal journal,
        IUserDataEraser eraser)
    {
        _userDatabases = userDatabases ?? throw new ArgumentNullException(nameof(userDatabases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _eraser = eraser ?? throw new ArgumentNullException(nameof(eraser));
    }

    /// <inheritdoc />
    public async Task EnsureCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        // Finish an interrupted account deletion before re-provisioning (idempotent; a no-op
        // unless a mark is owed) so the new, empty account never inherits stale residue.
        var key = StorageKeys.UserKey(_user.Iss, _user.Sub);
        if (await _journal.IsPendingAsync(key, cancellationToken).ConfigureAwait(false))
        {
            await _eraser.EraseAsync(key, cancellationToken).ConfigureAwait(false);
        }

        await using var repo = await _userDatabases
            .OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        // Refresh the username only when it actually changed - keep the steady-state
        // path read-only (no per-request write/WAL churn).
        var current = await repo.GetUsernameAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current, _user.Username, StringComparison.Ordinal))
        {
            await repo.SetUsernameAsync(_user.Username, cancellationToken).ConfigureAwait(false);
        }

        if (await repo.GetProjectAsync(DefaultProjectId, cancellationToken).ConfigureAwait(false) is null)
        {
            // Injected clock (dotnet-style-guide.md section 5) so tests can pin the timestamps.
            var now = _time.GetUtcNow();
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
