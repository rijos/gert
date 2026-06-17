using Gert.Chat;
using Gert.Model;
using Gert.Model.Chat;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// <c>GET /api/models</c> - the chat provider catalog for the picker (rest-api.md
/// section models). Just the <see cref="IChatProviderCatalog"/> port on the wire: operator
/// config (<c>Gert:Chat:Providers</c>) with the single-vLLM fallback, resolved in
/// <c>Gert.Chat.ConfigChatProviderCatalog</c> - the same list that gates
/// tool offering in the turn planner. Each entry's <c>id</c> is the provider slug a
/// conversation stores; secrets (api keys) stay host-side and never reach the wire.
/// </summary>
[ApiController]
[Route("api/models")]
public sealed class ModelsController : ControllerBase
{
    private readonly IChatProviderCatalog _catalog;

    public ModelsController(IChatProviderCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>List the configured providers (operator catalog, vLLM fallback).</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<ChatProviderInfo>> List() => Ok(_catalog.List());
}
