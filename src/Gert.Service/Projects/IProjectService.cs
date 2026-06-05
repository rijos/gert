using Gert.Model.Dtos;
using Gert.Model.Projects;

namespace Gert.Service.Projects;

/// <summary>
/// Manages the caller's projects — folders with their own <c>chat.db</c> /
/// <c>rag.db</c> / memory (rest-api.md § projects; configuration.md § 2).
/// Project config lives in <c>projects/{pid}/meta.json</c>, not a database.
/// </summary>
public interface IProjectService
{
    /// <summary>List the user's projects (reads <c>projects/*/meta.json</c>): id, name, counts, updated_at.</summary>
    Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Get one project's config + counts (conversations, documents, memory).</summary>
    Task<ProjectSummary?> GetAsync(string pid, CancellationToken cancellationToken = default);

    /// <summary>Create a new isolated project folder.</summary>
    Task<ProjectMeta> CreateAsync(
        CreateProjectRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Apply a partial update (rename / edit instructions / edit defaults).</summary>
    Task<ProjectMeta?> UpdateAsync(
        string pid,
        UpdateProjectRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a project (its chats and documents). The <c>default</c> project is
    /// emptied, not removed (configuration.md § 5).
    /// </summary>
    Task<bool> DeleteAsync(string pid, CancellationToken cancellationToken = default);
}
