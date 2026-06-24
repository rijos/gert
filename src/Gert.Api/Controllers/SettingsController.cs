using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Service;
using Gert.Service.Projects;
using Gert.Validation;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// User-level settings (rest-api.md section settings). Not project-scoped - identity is
/// implicit from the token (<see cref="IUserContext"/>). Covered by the fallback
/// authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    // Granular interface, not the IGertServices hub (dotnet-style-guide.md section 4).
    private readonly ISettingsService _settings;
    private readonly IValidationProvider _validation;

    public SettingsController(ISettingsService settings, IValidationProvider validation)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
    }

    /// <summary>Get the caller's current preferences.</summary>
    [HttpGet]
    public async Task<ActionResult<UserSettings>> Get(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        return Ok(settings);
    }

    /// <summary>Apply a partial update and return the merged result.</summary>
    [HttpPut]
    public async Task<ActionResult<UserSettings>> Update(
        [FromBody] UpdateSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.UpdateAsync(_validation.Prove(request), cancellationToken).ConfigureAwait(false);
        return Ok(settings);
    }
}
