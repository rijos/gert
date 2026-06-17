namespace Gert.Storage;

/// <summary>
/// A durable write-ahead record of account deletions that are <b>owed</b> - the recovery
/// journal that makes erasing a user crash-consistent across the independent stores
/// (user.db + chat.db engine, the rag.db engine, the object store), which may even sit on
/// separate volumes. There is no distributed transaction spanning a filesystem and a blob
/// store; instead deletion is a <b>saga with write-ahead intent + idempotent forward
/// recovery</b>: the eraser marks the user here <i>before</i> touching any store and clears
/// the mark only once every store is confirmed gone. If the process dies in between, the
/// mark survives, and a recovery sweep replays the (idempotent) erase to completion. Delete
/// has no meaningful "undo", so recovery only ever rolls forward.
///
/// <para>
/// The journal holds nothing but opaque, transient folder keys (no user data), and an entry
/// exists only while a delete is in flight - it is operational recovery state, not a central
/// user registry. Keyed by the <c>sha256(iss + sub)</c> folder key (security F6 shape).
/// </para>
/// </summary>
public interface IDeletionJournal
{
    /// <summary>Record that <paramref name="userKey"/>'s account deletion is owed (idempotent).</summary>
    Task MarkPendingAsync(string userKey, CancellationToken cancellationToken = default);

    /// <summary>Clear the owed-deletion mark for <paramref name="userKey"/> - the last step of a completed erase (idempotent).</summary>
    Task ClearAsync(string userKey, CancellationToken cancellationToken = default);

    /// <summary><see langword="true"/> if an account deletion for <paramref name="userKey"/> is owed (mid-flight or interrupted).</summary>
    Task<bool> IsPendingAsync(string userKey, CancellationToken cancellationToken = default);

    /// <summary>Every folder key with an owed deletion - the work list a recovery sweep re-drives to completion.</summary>
    Task<IReadOnlyList<string>> ListPendingAsync(CancellationToken cancellationToken = default);
}
