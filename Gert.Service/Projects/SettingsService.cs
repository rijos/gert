using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Service.Database;
using Gert.Service.Storage;
using Gert.Service.Validation;

namespace Gert.Service.Projects;

/// <summary>
/// Reads/writes the user's <c>settings.json</c> preferences (rest-api.md
/// § settings; configuration.md § 3) via <see cref="IUserStore"/>. User-level,
/// not project-scoped — identity comes only from <see cref="IUserContext"/>.
/// Provisioning is ensured first so the user folder + default <c>settings.json</c>
/// exist before a read/merge/write.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly IUserStore _store;
    private readonly IDatabaseProvider _databases;
    private readonly IValidationProvider _validation;
    private readonly IUserContext _user;

    public SettingsService(
        IUserStore store,
        IDatabaseProvider databases,
        IValidationProvider validation,
        IUserContext user)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public async Task<UserSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await _databases.EnsureProvisionedAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);
        return await _store.GetSettingsAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UserSettings> UpdateAsync(
        UpdateSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = _validation.Validate(request);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation);
        }

        await _databases.EnsureProvisionedAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        var current = await _store.GetSettingsAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        // Merge: each request field overrides only when present (null = leave unchanged).
        var merged = current with
        {
            Theme = request.Theme ?? current.Theme,
            UiLanguage = request.UiLanguage ?? current.UiLanguage,
            ReplyLanguage = request.ReplyLanguage ?? current.ReplyLanguage,
            DefaultModelId = request.DefaultModelId ?? current.DefaultModelId,
            DefaultTools = request.DefaultTools ?? current.DefaultTools,
            MemoryMode = request.MemoryMode ?? current.MemoryMode,
        };

        await _store.SaveSettingsAsync(_user.Iss, _user.Sub, merged, cancellationToken).ConfigureAwait(false);
        return merged;
    }
}
