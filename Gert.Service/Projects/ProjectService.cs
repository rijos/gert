using Gert.Model.Dtos;
using Gert.Model.Projects;

namespace Gert.Service.Projects;

/// <summary>
/// Stub. Projects are filesystem folders — <c>list/create</c> read/write
/// <c>projects/{pid}/meta.json</c> under the user's data root. The data-root
/// seam isn't wired into the service layer yet, so this is deferred.
/// <para>
/// // TODO U7c: implement against the host-provided data root
/// (read <c>projects/*/meta.json</c> for list, write a new folder + meta.json
/// for create, roll up counts via <see cref="Database.IDatabaseProvider"/>).
/// </para>
/// Present so <see cref="GertServices"/> + DI compile for the M1 gate.
/// </summary>
public sealed class ProjectService : IProjectService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");

    /// <inheritdoc />
    public Task<ProjectSummary?> GetAsync(string pid, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");

    /// <inheritdoc />
    public Task<ProjectMeta> CreateAsync(
        CreateProjectRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");

    /// <inheritdoc />
    public Task<ProjectMeta?> UpdateAsync(
        string pid,
        UpdateProjectRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string pid, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");
}
