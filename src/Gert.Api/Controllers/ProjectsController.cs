using Gert.Api.Validation;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Service.Projects;
using Gert.Validation;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// The caller's projects (rest-api.md section projects). The user is implicit (token);
/// <c>{pid}</c> is request-supplied but validated to a UUID/<c>default</c> and only
/// ever resolved inside the caller's own folder (configuration.md section 2.5). The
/// <c>default</c> project is emptied on delete, not removed. Covered by the fallback
/// authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects")]
public sealed class ProjectsController : ControllerBase
{
    // Granular interface, not the IGertServices hub (dotnet-style-guide.md section 4).
    private readonly IProjectService _projects;
    private readonly IValidationProvider _validation;

    public ProjectsController(IProjectService projects, IValidationProvider validation)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
    }

    /// <summary>
    /// List the caller's projects (id, name, counts, updated_at). <c>q</c>
    /// filters by name; <c>limit</c>/<c>offset</c> page for the search
    /// overlay's infinite scroll (limit 0 = all, capped at 100).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectSummary>>> List(
        [FromQuery] string? q = null,
        [FromQuery] int limit = 0,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var projects = await _projects
            .ListAsync(q, limit, Math.Max(offset, 0), cancellationToken)
            .ConfigureAwait(false);
        return Ok(projects);
    }

    /// <summary>Create a new isolated project.</summary>
    [HttpPost]
    public async Task<ActionResult<ProjectMeta>> Create(
        [FromBody] CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _projects.CreateAsync(_validation.Prove(request), cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { pid = created.Id }, created);
    }

    /// <summary>Get one project's config + counts.</summary>
    [HttpGet("{pid}")]
    public async Task<ActionResult<ProjectSummary>> Get(string pid, CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var project = await _projects.GetAsync(pid, cancellationToken).ConfigureAwait(false);
        return project is null ? NotFound() : Ok(project);
    }

    /// <summary>Apply a partial update (rename / edit instructions / defaults).</summary>
    [HttpPatch("{pid}")]
    public async Task<ActionResult<ProjectMeta>> Update(
        string pid,
        [FromBody] UpdateProjectRequest request,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var updated = await _projects.UpdateAsync(pid, _validation.Prove(request), cancellationToken).ConfigureAwait(false);
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>Delete a project (chats + documents). <c>default</c> is emptied, not removed.</summary>
    [HttpDelete("{pid}")]
    public async Task<IActionResult> Delete(string pid, CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var deleted = await _projects.DeleteAsync(pid, cancellationToken).ConfigureAwait(false);
        return deleted ? NoContent() : NotFound();
    }
}
