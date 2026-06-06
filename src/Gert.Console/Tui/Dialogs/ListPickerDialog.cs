using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Gert.Console.Tui.Dialogs;

/// <summary>
/// A small modal list picker (U16) — projects, models. Runs a nested modal
/// loop and returns the chosen index, or null on cancel.
/// </summary>
public static class ListPickerDialog
{
    /// <summary>Show the picker and block (modally) for a choice.</summary>
    public static int? Show(IApplication application, string title, IReadOnlyList<string> items, int selected = 0)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(items);

        int? result = null;

        using var dialog = new Dialog
        {
            Title = title,
            Width = Dim.Percent(60),
            Height = Math.Min(items.Count + 4, 16),
        };

        var list = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        list.SetSource(new ObservableCollection<string>(items));
        if (items.Count > 0)
        {
            list.SelectedItem = Math.Clamp(selected, 0, items.Count - 1);
        }

        list.KeyDown += (_, key) =>
        {
            if (key == Key.Enter)
            {
                key.Handled = true;
                result = list.SelectedItem;
                application.RequestStop(dialog);
            }
            else if (key == Key.Esc)
            {
                key.Handled = true;
                application.RequestStop(dialog);
            }
        };

        dialog.Add(list);
        list.SetFocus();

        application.Run(dialog);
        return result;
    }
}
