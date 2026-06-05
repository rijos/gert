using Gert.Api.Validation;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Service;
using Gert.Service.Projects;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// The caller's projects (rest-api.md § projects). The user is implicit (token);
/// <c>{pid}</c> is request-supplied but validated to a UUID/<c>default</c> and only
/// ever resolved inside the caller's own folder (configuration.md §2.5). The
/// <c>default</c> project is emptied on delete, not removed. Covered by the fallback
/// authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects")]
public sealed class ProjectsController : ControllerBase
{
    private readonly IGertServices _services;

    public ProjectsController(IGertServices services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>List the caller's projects (id, name, counts, updated_at).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectSummary>>> List(CancellationToken cancellationToken)
    {
        var projects = await _services.Projects.ListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(projects);
    }

    /// <summary>Create a new isolated project.</summary>
    [HttpPost]
    public async Task<ActionResult<ProjectMeta>> Create(
        [FromBody] CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _services.Projects.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { pid = created.Id }, created);
    }

    /// <summary>Get one project's config + counts.</summary>
    [HttpGet("{pid}")]
    public async Task<ActionResult<ProjectSummary>> Get(string pid, CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var project = await _services.Projects.GetAsync(pid, cancellationToken).ConfigureAwait(false);
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

        var updated = await _services.Projects.UpdateAsync(pid, request, cancellationToken).ConfigureAwait(false);
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>Delete a project (chats + documents). <c>default</c> is emptied, not removed.</summary>
    [HttpDelete("{pid}")]
    public async Task<IActionResult> Delete(string pid, CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var deleted = await _services.Projects.DeleteAsync(pid, cancellationToken).ConfigureAwait(false);
        return deleted ? NoContent() : NotFound();
    }
}
