using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Service;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// User-level settings (rest-api.md § settings). Not project-scoped — identity is
/// implicit from the token (<see cref="IUserContext"/>). Covered by the fallback
/// authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly IGertServices _services;

    public SettingsController(IGertServices services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>Get the caller's current preferences.</summary>
    [HttpGet]
    public async Task<ActionResult<UserSettings>> Get(CancellationToken cancellationToken)
    {
        var settings = await _services.Settings.GetAsync(cancellationToken).ConfigureAwait(false);
        return Ok(settings);
    }

    /// <summary>Apply a partial update and return the merged result.</summary>
    [HttpPut]
    public async Task<ActionResult<UserSettings>> Update(
        [FromBody] UpdateSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await _services.Settings.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
        return Ok(settings);
    }
}
