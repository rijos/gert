using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Gert.Console.Tui.Dialogs;

/// <summary>
/// A small modal text prompt (U16) — rename conversation, new project name,
/// settings fields. Runs a nested modal loop on the UI thread and returns the
/// entered text, or null on cancel.
/// </summary>
public static class InputDialog
{
    /// <summary>Show the prompt and block (modally) for the answer.</summary>
    public static string? Show(IApplication application, string title, string label, string initial = "")
    {
        ArgumentNullException.ThrowIfNull(application);

        string? result = null;

        using var dialog = new Dialog
        {
            Title = title,
            Width = Dim.Percent(60),
            Height = 7,
        };

        var prompt = new Label { X = 1, Y = 1, Text = label };
        var field = new TextField
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Text = initial,
        };

        var ok = new Button { Title = "OK", IsDefault = true };
        ok.Accepting += (_, e) =>
        {
            result = field.Text;
            e.Handled = true;
            application.RequestStop(dialog);
        };

        var cancel = new Button { Title = "Cancel" };
        cancel.Accepting += (_, e) =>
        {
            e.Handled = true;
            application.RequestStop(dialog);
        };

        dialog.Add(prompt, field);
        dialog.AddButton(ok);
        dialog.AddButton(cancel);
        field.SetFocus();

        application.Run(dialog);

        var text = result?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
