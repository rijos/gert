using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Gert.Console.Tui.Dialogs;

/// <summary>A minimal modal message box (U16) — errors and confirmations.</summary>
public static class MessageDialog
{
    /// <summary>Show the message with a single OK button.</summary>
    public static void Show(IApplication application, string title, string message)
    {
        ArgumentNullException.ThrowIfNull(application);

        using var dialog = new Dialog
        {
            Title = title,
            Width = Dim.Percent(50),
            Height = 8,
        };

        dialog.Add(new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            Text = message,
        });

        var ok = new Button { Title = "OK", IsDefault = true };
        ok.Accepting += (_, e) =>
        {
            e.Handled = true;
            application.RequestStop(dialog);
        };
        dialog.AddButton(ok);

        application.Run(dialog);
    }
}
