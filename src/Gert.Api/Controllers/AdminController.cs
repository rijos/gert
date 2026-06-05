using Gert.Api.Validation;
using Gert.Authentication;
using Gert.Model.Projects;
using Gert.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// The admin data-lifecycle surface (rest-api.md § admin; auth.md § matrix) — the
/// only endpoints gated by the <see cref="GertAuthorizationExtensions.AdminPolicy"/>
/// (<c>RequireRole("gert-admins")</c>). Admin grants <b>no</b> cross-user data read:
/// these only scan folder <c>meta.json</c> and <c>rm -rf</c> a directory. The
/// <c>{key}</c> is the most dangerous parameter in the API — it feeds a
/// <c>rm -rf</c> — so it is validated to <c>^[0-9a-f]{64}$</c> (security F6)
/// <b>before</b> it is ever handed to the service / path-joined.
/// </summary>
[ApiController]
[Authorize(Policy = GertAuthorizationExtensions.AdminPolicy)]
[Route("api/admin/users")]
public sealed class AdminController : ControllerBase
{
    private readonly IGertServices _services;

    public AdminController(IGertServices services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>List user folders by reading each <c>meta.json</c>.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserSummary>>> List(CancellationToken cancellationToken)
    {
        var users = await _services.Admin.ListUsersAsync(cancellationToken).ConfigureAwait(false);
        return Ok(users);
    }

    /// <summary>One user's folder summary by validated <c>{key}</c>.</summary>
    [HttpGet("{key}")]
    public async Task<ActionResult<UserSummary>> Get(string key, CancellationToken cancellationToken)
    {
        RouteParams.RequireValidAdminKey(key);

        var user = await _services.Admin.GetUserAsync(key, cancellationToken).ConfigureAwait(false);
        return user is null ? NotFound() : Ok(user);
    }

    /// <summary><c>rm -rf /data/users/{key}</c> after the <c>{key}</c> shape guard (F6).</summary>
    [HttpDelete("{key}")]
    public async Task<IActionResult> Delete(string key, CancellationToken cancellationToken)
    {
        RouteParams.RequireValidAdminKey(key);

        var deleted = await _services.Admin.DeleteUserAsync(key, cancellationToken).ConfigureAwait(false);
        return deleted ? NoContent() : NotFound();
    }
}
