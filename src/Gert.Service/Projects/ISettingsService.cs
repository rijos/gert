using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Service.Validation;

namespace Gert.Service.Projects;

/// <summary>
/// Reads/writes the user's preferences (the <c>user.db</c> settings row; rest-api.md
/// section settings; configuration.md section 3). User-level, not project-scoped.
/// </summary>
public interface ISettingsService
{
    /// <summary>Get the user's current preferences.</summary>
    Task<UserSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Apply a partial update and return the merged result.</summary>
    Task<UserSettings> UpdateAsync(
        Validated<UpdateSettingsRequest> request,
        CancellationToken cancellationToken = default);
}
