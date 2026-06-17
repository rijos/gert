using Gert.Api.Validation;
using Gert.Authentication;
using Gert.Model.Projects;
using Gert.Service.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// The admin data-lifecycle surface (rest-api.md section admin; auth.md section matrix) - the
/// only endpoints gated by the <see cref="GertAuthorizationExtensions.AdminPolicy"/>
/// (<c>RequireRole("gert-admins")</c>). Admin grants <b>no</b> cross-user data read:
/// these only scan folder footprints (sizes/counts, plus the username from each
/// user's <c>user.db</c>) and delete a user's data. The
/// <c>{key}</c> is the most dangerous parameter in the API - it feeds a destructive
/// whole-account delete - so it is validated to <c>^[0-9a-f]{64}$</c> (security F6)
/// <b>before</b> it is ever handed to the service / path-joined.
/// </summary>
[ApiController]
[Authorize(Policy = GertAuthorizationExtensions.AdminPolicy)]
[Route("api/admin/users")]
public sealed class AdminController : ControllerBase
{
    // Granular interface, not the IGertServices hub (dotnet-style-guide.md section 4).
    private readonly IAdminService _admin;

    public AdminController(IAdminService admin) =>
        _admin = admin ?? throw new ArgumentNullException(nameof(admin));

    /// <summary>List user folders: blob footprint + the <c>user.db</c> username.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserSummary>>> List(CancellationToken cancellationToken)
    {
        var users = await _admin.ListUsersAsync(cancellationToken).ConfigureAwait(false);
        return Ok(users);
    }

    /// <summary>One user's folder summary by validated <c>{key}</c>.</summary>
    [HttpGet("{key}")]
    public async Task<ActionResult<UserSummary>> Get(string key, CancellationToken cancellationToken)
    {
        RouteParams.RequireValidAdminKey(key);

        var user = await _admin.GetUserAsync(key, cancellationToken).ConfigureAwait(false);
        return user is null ? NotFound() : Ok(user);
    }

    /// <summary>Delete all of that user's data (service drops the db halves then the blob scope) after the <c>{key}</c> shape guard (F6).</summary>
    [HttpDelete("{key}")]
    public async Task<IActionResult> Delete(string key, CancellationToken cancellationToken)
    {
        RouteParams.RequireValidAdminKey(key);

        var deleted = await _admin.DeleteUserAsync(key, cancellationToken).ConfigureAwait(false);
        return deleted ? NoContent() : NotFound();
    }
}
