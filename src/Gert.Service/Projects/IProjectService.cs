using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Validation;

namespace Gert.Service.Projects;

/// <summary>
/// Manages the caller's projects - folders with their own <c>chat.db</c> /
/// <c>rag.db</c> (rest-api.md section projects; configuration.md section 2).
/// Project config (id, name, instructions, defaults) is a row in the
/// <c>user.db</c> project registry, not a JSON sidecar (storage-and-data.md
/// section "No JSON sidecars").
/// </summary>
public interface IProjectService
{
    /// <summary>
    /// List the user's projects (the <c>user.db</c> registry): id, name, counts,
    /// updated_at. <paramref name="query"/> filters by name (case-insensitive
    /// contains); <paramref name="limit"/> (0 = all, capped at 100) and
    /// <paramref name="offset"/> page the result.
    /// </summary>
    Task<IReadOnlyList<ProjectSummary>> ListAsync(
        string? query = null,
        int limit = 0,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Get one project's config + counts (conversations, documents).</summary>
    Task<ProjectSummary?> GetAsync(string pid, CancellationToken cancellationToken = default);

    /// <summary>Create a new isolated project folder.</summary>
    Task<ProjectMeta> CreateAsync(
        Validated<CreateProjectRequest> request,
        CancellationToken cancellationToken = default);

    /// <summary>Apply a partial update (rename / edit instructions / edit defaults).</summary>
    Task<ProjectMeta?> UpdateAsync(
        string pid,
        Validated<UpdateProjectRequest> request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a project (its chats and documents). The <c>default</c> project is
    /// emptied, not removed (configuration.md section 5).
    /// </summary>
    Task<bool> DeleteAsync(string pid, CancellationToken cancellationToken = default);
}
