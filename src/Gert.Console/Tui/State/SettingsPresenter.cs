using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Service;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Console.Tui.State;

/// <summary>
/// Settings access for the TUI (U16) — the console analog of
/// <c>settings-modal.js</c> + <c>model-settings-modal.js</c>: load the user's
/// <c>settings.json</c>, apply partial updates (reply language, default
/// model, per-model GenerationParams).
/// </summary>
public sealed class SettingsPresenter
{
    private readonly IServiceProvider _services;

    public SettingsPresenter(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>The user's current preferences.</summary>
    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
        return await gert.Settings.GetAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Apply a partial update; returns the merged result.</summary>
    public async Task<UserSettings> SaveAsync(
        UpdateSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var scope = _services.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
        return await gert.Settings.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
