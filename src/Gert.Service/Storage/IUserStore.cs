using Gert.Model.Projects;

namespace Gert.Service.Storage;

/// <summary>
/// The user/project <b>config + directory</b> seam (storage-and-data.md § layout).
/// Owns the read/write of the two config files — the user's <c>settings.json</c>
/// and each project's <c>projects/{pid}/meta.json</c> — and the coarse directory
/// lifecycle (enumerate users, <c>rm -rf</c> a project or user folder). These are
/// config files and directory operations, not user file blobs (those go through
/// <see cref="IObjectStore"/>), and not databases (those go through
/// <see cref="Database.IDatabaseProvider"/>).
///
/// <para>
/// Identity is threaded exactly as the other seams: <c>(iss, sub)</c> come only
/// from the validated token (<see cref="IUserContext"/>) and <c>pid</c> is a
/// validated UUID or the literal <c>default</c>, so an operation can never select
/// another user's or project's data. The host-agnostic service layer depends only
/// on this port; the local-persistence adapter
/// (<c>Gert.Database.FileSystemUserStore</c>) implements it over the
/// filesystem and a future object-storage backend is a drop-in.
/// </para>
/// </summary>
public interface IUserStore
{
    // ---- provisioning (user root + project skeleton on disk) ---------------

    /// <summary>
    /// Ensure the user root's on-disk skeleton: the directory, the descriptive
    /// <c>meta.json</c> sidecar (written when missing or unreadable — a healthy
    /// file is left alone so <c>created_at</c> survives), and a default
    /// <c>settings.json</c> when absent. The caller (the database provider's
    /// fail-closed provisioning gate) has already validated <c>(iss, sub)</c>;
    /// this method only does file work.
    /// </summary>
    Task EnsureUserFilesAsync(string iss, string sub, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensure a project's on-disk skeleton: <c>projects/{pid}/</c> with its
    /// <c>files/</c> and <c>memory/</c> directories, and a default
    /// <c>meta.json</c> when absent. Database files are NOT touched — they
    /// belong to <see cref="Database.IDatabaseProvider"/>.
    /// </summary>
    Task EnsureProjectFilesAsync(string iss, string sub, string pid, CancellationToken cancellationToken = default);

    // ---- user settings (settings.json) ------------------------------------

    /// <summary>
    /// Read the user's <c>settings.json</c>. Returns defaults when the file is
    /// absent (the user folder may not be provisioned yet — the caller ensures it).
    /// </summary>
    Task<UserSettings> GetSettingsAsync(string iss, string sub, CancellationToken cancellationToken = default);

    /// <summary>Write the user's <c>settings.json</c>, overwriting any existing file.</summary>
    Task SaveSettingsAsync(string iss, string sub, UserSettings settings, CancellationToken cancellationToken = default);

    // ---- project config (projects/{pid}/meta.json) ------------------------

    /// <summary>List the user's projects by reading each <c>projects/*/meta.json</c>.</summary>
    Task<IReadOnlyList<ProjectMeta>> ListProjectsAsync(string iss, string sub, CancellationToken cancellationToken = default);

    /// <summary>Read one project's <c>meta.json</c>, or <see langword="null"/> if it does not exist.</summary>
    Task<ProjectMeta?> GetProjectAsync(string iss, string sub, string pid, CancellationToken cancellationToken = default);

    /// <summary>Write a project's <c>meta.json</c> (<see cref="ProjectMeta.Id"/> is the pid), overwriting.</summary>
    Task SaveProjectAsync(string iss, string sub, ProjectMeta meta, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>rm -rf projects/{pid}</c> — remove the whole project directory. Returns
    /// <see langword="true"/> if a directory was removed, <see langword="false"/> if
    /// none existed (idempotent).
    /// </summary>
    Task<bool> DeleteProjectAsync(string iss, string sub, string pid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Empty a project directory's contents while keeping the directory itself — the
    /// <c>default</c>-project path (configuration.md § 5): the landing project is
    /// emptied, never removed.
    /// </summary>
    Task EmptyProjectAsync(string iss, string sub, string pid, CancellationToken cancellationToken = default);

    // ---- account (users/{key}) --------------------------------------------

    /// <summary>
    /// <c>rm -rf users/{key}</c> for the caller — erase all of this user's data.
    /// Returns <see langword="true"/> if the folder existed and was removed.
    /// </summary>
    Task<bool> DeleteUserAsync(string iss, string sub, CancellationToken cancellationToken = default);

    // ---- admin (scan users/*/meta.json) -----------------------------------

    /// <summary>Scan <c>{DataRoot}/users/*/meta.json</c> and summarise each user folder.</summary>
    Task<IReadOnlyList<UserSummary>> ListUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>One user-folder summary by its folder <paramref name="key"/>, or <see langword="null"/>.</summary>
    Task<UserSummary?> GetUserAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>rm -rf users/{key}</c> by folder key (admin). The caller has already
    /// shape-validated <paramref name="key"/> (<c>^[0-9a-f]{64}$</c>, security F6);
    /// the adapter additionally asserts the resolved path stays under
    /// <c>{DataRoot}/users</c> before deleting. Returns <see langword="true"/> if a
    /// folder was removed.
    /// </summary>
    Task<bool> DeleteUserByKeyAsync(string key, CancellationToken cancellationToken = default);
}
