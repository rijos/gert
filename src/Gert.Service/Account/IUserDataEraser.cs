namespace Gert.Service.Account;

/// <summary>
/// Erases <b>all</b> of one user's data across the independent stores - the structured
/// databases (<c>user.db</c> + <c>chat.db</c>), the RAG index (<c>rag.db</c>), and the
/// object store (file/memory blobs), which may sit on separate volumes - as a single,
/// crash-consistent operation. It is guarded by the <see cref="Gert.Storage.IDeletionJournal"/>:
/// the user is marked owed <i>before</i> any store is touched and cleared only once every
/// store is confirmed gone, so an interrupted erase is resumable and idempotent (a recovery
/// sweep, or the next provisioning of the same user, replays it to completion). One code path
/// for self-service account delete, admin delete, and recovery, addressed by the
/// <c>sha256(iss + sub)</c> folder key.
/// </summary>
public interface IUserDataEraser
{
    /// <summary>
    /// Erase everything stored for <paramref name="userKey"/> and clear its journal mark.
    /// Idempotent - safe to re-run against an already-erased (or partially-erased) user.
    /// Returns <see langword="true"/> if any state existed. The key is shape-validated
    /// (security F6) before any path is formed.
    /// </summary>
    Task<bool> EraseAsync(string userKey, CancellationToken cancellationToken = default);
}
