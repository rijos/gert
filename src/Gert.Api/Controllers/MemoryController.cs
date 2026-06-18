using Gert.Api.Validation;
using Gert.Model.Dtos;
using Gert.Model.Rag;
using Gert.Service.Documents;
using Gert.Service.Validation;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// Project-scoped memory entries (rest-api.md section memory) - markdown notes embedded
/// into the project's <c>rag.db</c> as <c>kind='memory'</c>. Every action validates
/// <c>{pid}</c> first. Covered by the fallback authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}/memory")]
public sealed class MemoryController : ControllerBase
{
    // Granular interface, not the IGertServices hub (dotnet-style-guide.md section 4).
    private readonly IMemoryService _memory;
    private readonly IValidationProvider _validation;

    public MemoryController(IMemoryService memory, IValidationProvider validation)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
    }

    /// <summary>List entries (id, title, pinned, updated_at).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MemoryEntry>>> List(
        string pid,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var entries = await _memory.ListAsync(pid, cancellationToken).ConfigureAwait(false);
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

        var entry = await _memory.UpsertAsync(pid, _validation.Prove(request), cancellationToken).ConfigureAwait(false);
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

        var deleted = await _memory.DeleteAsync(pid, id, cancellationToken).ConfigureAwait(false);
        return deleted ? NoContent() : NotFound();
    }
}
