using Gert.Model.Projects;

namespace Gert.Database;

/// <summary>
/// Per-user <c>user.db</c> persistence (storage-and-data.md section user.db). One
/// instance wraps an open connection to a single user's database; dispose it when
/// the unit of work completes (open-per-use). The connection's path is the scope -
/// there is no <c>(iss, sub)</c> argument, so a query structurally cannot reach
/// another user's rows.
///
/// <para>
/// Holds the user's durable, transactional state: the username (for the admin scan),
/// the user's <see cref="UserSettings"/>, and the project registry
/// (<see cref="ProjectMeta"/> rows). Per-project conversation data
/// lives in its own <c>chat.db</c> (<see cref="IChatDatabaseProvider"/>); the RAG index
/// is a separate capability (<c>Gert.Rag.IRagIndexProvider</c>).
/// </para>
/// </summary>
public interface IUserRepository : IAsyncDisposable
{
    // ---- user meta (admin scan) -------------------------------------------

    /// <summary>The display username, or <see langword="null"/> if never seeded.</summary>
    Task<string?> GetUsernameAsync(CancellationToken cancellationToken = default);

    /// <summary>Set the display username (refreshed from the token by the provisioner).</summary>
    Task SetUsernameAsync(string username, CancellationToken cancellationToken = default);

    // ---- settings ----------------------------------------------------------

    /// <summary>Read the user's settings, or defaults when none have been saved.</summary>
    Task<UserSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Write the user's settings, replacing any existing row.</summary>
    Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default);

    // ---- project registry --------------------------------------------------

    /// <summary>List the user's projects, oldest first.</summary>
    Task<IReadOnlyList<ProjectMeta>> ListProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>Read one project by id, or <see langword="null"/> if it is not registered.</summary>
    Task<ProjectMeta?> GetProjectAsync(string pid, CancellationToken cancellationToken = default);

    /// <summary>Insert or replace a project row (<see cref="ProjectMeta.Id"/> is the pid).</summary>
    Task SaveProjectAsync(ProjectMeta meta, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a project row. Returns <see langword="true"/> if a row existed
    /// (idempotent). Does not touch the project's <c>chat.db</c>/<c>rag.db</c> or
    /// its blobs - that cleanup is the caller's.
    /// </summary>
    Task<bool> DeleteProjectAsync(string pid, CancellationToken cancellationToken = default);
}
