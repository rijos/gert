using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// <c>GET /api/models</c> — the model catalog for the picker (rest-api.md § models).
/// For the M1 walking skeleton this returns an empty list; the config-driven vLLM
/// catalog lands with the external adapters (U10).
/// </summary>
[ApiController]
[Route("api/models")]
public sealed class ModelsController : ControllerBase
{
    /// <summary>One entry in the model catalog (rest-api.md § models).</summary>
    public sealed record ModelInfo(
        string Id,
        string Name,
        bool Default,
        IReadOnlyList<string> Capabilities,
        int Context);

    /// <summary>List the configured models. Empty for M1 (no external catalog yet).</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<ModelInfo>> List() =>
        Ok(Array.Empty<ModelInfo>());
}
