using Gert.Database;
using Gert.Rag;
using Gert.Storage;

namespace Gert.Service.Account;

/// <summary>
/// The single, journal-guarded erase path (<see cref="IUserDataEraser"/>). True ACID across
/// a filesystem and a blob store isn't reachable, so this is a <b>saga with write-ahead
/// intent + idempotent forward recovery</b>: mark the user owed, drop each store's data,
/// clear the mark last. A crash anywhere leaves the mark set, and replaying the (idempotent)
/// erase converges to fully-deleted - delete only ever rolls forward, never undoes.
/// </summary>
public sealed class UserDataEraser : IUserDataEraser
{
    private readonly IUserDatabaseProvider _userDatabases;
    private readonly IRagIndexProvider _ragDatabases;
    private readonly IObjectStore _objects;
    private readonly IDeletionJournal _journal;

    public UserDataEraser(
        IUserDatabaseProvider userDatabases,
        IRagIndexProvider ragDatabases,
        IObjectStore objects,
        IDeletionJournal journal)
    {
        _userDatabases = userDatabases ?? throw new ArgumentNullException(nameof(userDatabases));
        _ragDatabases = ragDatabases ?? throw new ArgumentNullException(nameof(ragDatabases));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    /// <inheritdoc />
    public async Task<bool> EraseAsync(string userKey, CancellationToken cancellationToken = default)
    {
        // Re-assert the F6 shape even though every caller already validated it: the key
        // names a marker and three store roots, so it must never be a path or a prefix.
        StorageKeys.ValidateUserKey(userKey);

        // 1. Durable intent FIRST. If the process dies after this, the mark survives and the
        //    recovery sweep (or the user's next provisioning) replays this erase.
        await _journal.MarkPendingAsync(userKey, cancellationToken).ConfigureAwait(false);

        // 2. Each engine drops only its OWN files/rows - database halves before the blob
        //    tree, so a local whole-tree wipe never races a still-open db handle. Every step
        //    is idempotent, so a replay after a partial crash is safe.
        var dbRemoved = await _userDatabases.DeleteUserByKeyAsync(userKey, cancellationToken).ConfigureAwait(false);
        var ragRemoved = await _ragDatabases.DeleteUserByKeyAsync(userKey, cancellationToken).ConfigureAwait(false);
        var blobsRemoved = await _objects
            .DeleteScopeAsync(ObjectScope.FromUserKey(userKey), cancellationToken).ConfigureAwait(false);

        // 3. Clear the intent LAST - only once every store reports done, so the mark can
        //    never be cleared while residue remains.
        await _journal.ClearAsync(userKey, cancellationToken).ConfigureAwait(false);

        return dbRemoved || ragRemoved || blobsRemoved;
    }
}
