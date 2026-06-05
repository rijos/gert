using Gert.Model;
using Gert.Service.External;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// <c>GET /api/models</c> — the model catalog for the picker (rest-api.md § models).
/// Just the <see cref="IModelCatalog"/> port on the wire: operator config
/// (<c>Gert:Models</c>) with the single-vLLM fallback, resolved in
/// <c>Gert.External.ConfigModelCatalog</c> — the same list that gates tool
/// offering in the turn planner.
/// </summary>
[ApiController]
[Route("api/models")]
public sealed class ModelsController : ControllerBase
{
    private readonly IModelCatalog _catalog;

    public ModelsController(IModelCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>List the configured models (operator catalog, vLLM fallback).</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<ModelInfo>> List() => Ok(_catalog.List());
}
