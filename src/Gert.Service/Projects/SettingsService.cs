using Gert.Database;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Validation;

namespace Gert.Service.Projects;

/// <summary>
/// Reads/writes the user's preferences (rest-api.md section settings; configuration.md
/// section 3) in <c>user.db</c> via <see cref="IUserDatabaseProvider"/>. User-level, not
/// project-scoped - identity comes only from <see cref="IUserContext"/>. The store
/// self-provisions on open, so a read returns durable defaults for a brand-new user
/// without any explicit provisioning step.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly IUserDatabaseProvider _userDatabases;
    private readonly IUserContext _user;

    public SettingsService(
        IUserDatabaseProvider userDatabases,
        IUserContext user)
    {
        _userDatabases = userDatabases ?? throw new ArgumentNullException(nameof(userDatabases));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public async Task<UserSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var repo = await _userDatabases
            .OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);
        return await repo.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UserSettings> UpdateAsync(
        Validated<UpdateSettingsRequest> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dto = request.Value;

        await using var repo = await _userDatabases
            .OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        var current = await repo.GetSettingsAsync(cancellationToken).ConfigureAwait(false);

        // Merge: each request field overrides only when present (null = leave unchanged).
        var merged = current with
        {
            Theme = dto.Theme ?? current.Theme,
            UiLanguage = dto.UiLanguage ?? current.UiLanguage,
            ReplyLanguage = dto.ReplyLanguage ?? current.ReplyLanguage,
            DefaultModelId = dto.DefaultModelId ?? current.DefaultModelId,
            DefaultTools = dto.DefaultTools ?? current.DefaultTools,
            MemoryMode = dto.MemoryMode ?? current.MemoryMode,
        };

        await repo.SaveSettingsAsync(merged, cancellationToken).ConfigureAwait(false);
        return merged;
    }
}
