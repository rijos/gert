using Gert.Model.Projects;

namespace Gert.Service.Storage;

/// <summary>
/// The user/project <b>blob lifecycle</b> seam (storage-and-data.md § layout). Now
/// that the structured config (username, settings, project registry) lives in
/// <c>user.db</c> (<see cref="Database.IUserDatabaseProvider"/>), this owns only the
/// coarse directory lifecycle over the object store: deleting or emptying a project
/// or user folder (which also removes that scope's <c>chat.db</c>/<c>rag.db</c>,
/// since they live under the same scope root) and the admin footprint scan.
///
/// <para>
/// Identity is threaded as on the other seams: <c>(iss, sub)</c> come only from the
/// validated token (hashed into the <see cref="ObjectScope"/>) and <c>pid</c> /
/// admin-supplied keys are shape-validated, so an operation can never select another
/// user's or project's data. The host-agnostic service layer depends only on this
/// port; the object-store backend (local FS → S3 → Azure) is a drop-in.
/// </para>
/// </summary>
public interface IUserStore
{
    // ---- project blob lifecycle -------------------------------------------

    /// <summary>
    /// <c>rm -rf projects/{pid}</c> — remove the whole project scope (blobs +
    /// <c>chat.db</c>/<c>rag.db</c>). Returns <see langword="true"/> if a directory
    /// was removed, <see langword="false"/> if none existed (idempotent).
    /// </summary>
    Task<bool> DeleteProjectAsync(string iss, string sub, string pid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Empty a project scope's contents while keeping the scope root — the
    /// <c>default</c>-project path (configuration.md § 5): the landing project is
    /// emptied, never removed. Its databases re-materialise on the next open.
    /// </summary>
    Task EmptyProjectAsync(string iss, string sub, string pid, CancellationToken cancellationToken = default);

    // ---- account (users/{key}) --------------------------------------------

    /// <summary>
    /// <c>rm -rf users/{key}</c> for the caller — erase all of this user's data.
    /// Returns <see langword="true"/> if the folder existed and was removed.
    /// </summary>
    Task<bool> DeleteUserAsync(string iss, string sub, CancellationToken cancellationToken = default);

    // ---- admin (scan user folders) ----------------------------------------

    /// <summary>Scan <c>{DataRoot}/users/*</c> and summarise each user folder's blob footprint.</summary>
    Task<IReadOnlyList<UserFootprint>> ListUserFootprintsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One user folder's footprint by its folder <paramref name="key"/>, or
    /// <see langword="null"/> if no such folder exists.
    /// </summary>
    Task<UserFootprint?> GetUserFootprintAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>rm -rf users/{key}</c> by folder key (admin). The caller has already
    /// shape-validated <paramref name="key"/> (<c>^[0-9a-f]{64}$</c>, security F6);
    /// the adapter additionally asserts the resolved path stays under
    /// <c>{DataRoot}/users</c> before deleting. Returns <see langword="true"/> if a
    /// folder was removed.
    /// </summary>
    Task<bool> DeleteUserByKeyAsync(string key, CancellationToken cancellationToken = default);
}
