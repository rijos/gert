using Gert.Api.Validation;
using Gert.Model.Dtos;
using Gert.Model.Rag;
using Gert.Service;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// Project-scoped memory entries (rest-api.md § memory) — markdown notes embedded
/// into the project's <c>rag.db</c> as <c>kind='memory'</c>. Every action validates
/// <c>{pid}</c> first. Covered by the fallback authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}/memory")]
public sealed class MemoryController : ControllerBase
{
    private readonly IGertServices _services;

    public MemoryController(IGertServices services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>List entries (id, title, pinned, updated_at).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MemoryEntry>>> List(
        string pid,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var entries = await _services.Memory.ListAsync(pid, cancellationToken).ConfigureAwait(false);
        return Ok(entries);
    }

    /// <summary>Add or edit an entry; it is (re)embedded for retrieval.</summary>
    [HttpPost]
    public async Task<ActionResult<MemoryEntry>> Upsert(
        string pid,
        [FromBody] CreateMemoryRequest request,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var entry = await _services.Memory.UpsertAsync(pid, request, cancellationToken).ConfigureAwait(false);
        return Ok(entry);
    }

    /// <summary>Remove an entry and its chunks.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(
        string pid,
        string id,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var deleted = await _services.Memory.DeleteAsync(pid, id, cancellationToken).ConfigureAwait(false);
        return deleted ? NoContent() : NotFound();
    }
}
