using Gert.Authentication;
using Gert.Service.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// Admin inspection of what the model is sent (rest-api.md section admin): the
/// built-in system prompt plus every registered tool spec, verbatim. Pure
/// configuration - per-project pinned instructions are user data and stay out
/// (auth.md section matrix: admin grants no cross-user data read). Read-only, so the
/// admin policy is the only gate; no route parameters to validate.
/// </summary>
[ApiController]
[Authorize(Policy = GertAuthorizationExtensions.AdminPolicy)]
[Route("api/admin/system-prompt")]
public sealed class AdminSystemPromptController : ControllerBase
{
    private readonly ISystemPromptInspector _inspector;

    public AdminSystemPromptController(ISystemPromptInspector inspector) =>
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));

    /// <summary>The system prompt + full tool specs as advertised to the model.</summary>
    [HttpGet]
    public ActionResult<SystemPromptSnapshot> Get() => Ok(_inspector.GetSnapshot());
}
