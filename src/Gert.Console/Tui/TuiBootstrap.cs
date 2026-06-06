using Terminal.Gui.App;

namespace Gert.Console.Tui;

/// <summary>
/// Boots the full-screen TUI (U16): <c>Application.Create()</c> →
/// <c>Init</c> → run the <see cref="TuiApp"/> shell → dispose. Kept
/// deliberately thin and driver-bound — everything interesting lives in the
/// headless presenters/state under <c>Tui/State</c>, which tests exercise
/// without a terminal.
/// </summary>
public static class TuiBootstrap
{
    /// <summary>
    /// Run the TUI event loop over the configured service graph until the user
    /// quits. Returns the process exit code.
    /// </summary>
    public static int Run(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var application = Application.Create();
        application.Init();

        using var app = new TuiApp(application, services);
        application.Run(app);

        return 0;
    }
}
