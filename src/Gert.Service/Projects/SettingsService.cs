using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Database;
using Gert.Service.Validation;

namespace Gert.Service.Projects;

/// <summary>
/// Reads/writes the user's preferences (rest-api.md § settings; configuration.md
/// § 3) in <c>user.db</c> via <see cref="IUserDatabaseProvider"/>. User-level, not
/// project-scoped — identity comes only from <see cref="IUserContext"/>. The store
/// self-provisions on open, so a read returns durable defaults for a brand-new user
/// without any explicit provisioning step.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly IUserDatabaseProvider _userDatabases;
    private readonly IValidationProvider _validation;
    private readonly IUserContext _user;

    public SettingsService(
        IUserDatabaseProvider userDatabases,
        IValidationProvider validation,
        IUserContext user)
    {
        _userDatabases = userDatabases ?? throw new ArgumentNullException(nameof(userDatabases));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
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
        UpdateSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = _validation.Validate(request);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation);
        }

        await using var repo = await _userDatabases
            .OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        var current = await repo.GetSettingsAsync(cancellationToken).ConfigureAwait(false);

        // Merge: each request field overrides only when present (null = leave unchanged).
        var merged = current with
        {
            Theme = request.Theme ?? current.Theme,
            UiLanguage = request.UiLanguage ?? current.UiLanguage,
            ReplyLanguage = request.ReplyLanguage ?? current.ReplyLanguage,
            DefaultModelId = request.DefaultModelId ?? current.DefaultModelId,
            DefaultTools = request.DefaultTools ?? current.DefaultTools,
            MemoryMode = request.MemoryMode ?? current.MemoryMode,
            ModelParams = MergeModelParams(current.ModelParams, request.ModelParams),
        };

        await repo.SaveSettingsAsync(merged, cancellationToken).ConfigureAwait(false);
        return merged;
    }

    /// <summary>
    /// Per-model merge: each supplied model id REPLACES that model's whole entry (the
    /// cogwheel modal sends the full params for one model; an all-unset entry
    /// effectively clears it); absent ids stay untouched.
    /// </summary>
    private static IReadOnlyDictionary<string, GenerationParams>? MergeModelParams(
        IReadOnlyDictionary<string, GenerationParams>? current,
        IReadOnlyDictionary<string, GenerationParams>? patch)
    {
        if (patch is null)
        {
            return current;
        }

        var merged = current is null
            ? new Dictionary<string, GenerationParams>(StringComparer.Ordinal)
            : new Dictionary<string, GenerationParams>(current, StringComparer.Ordinal);

        foreach (var (modelId, value) in patch)
        {
            merged[modelId] = value;
        }

        return merged;
    }
}
