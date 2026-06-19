namespace Gert.Storage;

/// <summary>
/// A durable write-ahead record of account deletions that are <b>owed</b>, making erasure
/// crash-consistent across the independent stores (user.db + chat.db engine, the rag.db
/// engine, the object store), which may sit on separate volumes with no distributed
/// transaction spanning them. Deletion is a saga with write-ahead intent + idempotent
/// forward recovery: the eraser marks the user here <i>before</i> touching any store and
/// clears the mark only once every store is confirmed gone, so a crash in between leaves the
/// mark for a recovery sweep to replay. Recovery only rolls forward (delete has no undo).
///
/// <para>
/// Holds nothing but opaque, transient folder keys - operational recovery state for in-flight
/// deletes, not a central user registry. Keyed by the <c>sha256(iss + sub)</c> folder key
/// (security F6 shape).
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
