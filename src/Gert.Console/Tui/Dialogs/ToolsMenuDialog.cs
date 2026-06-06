using System.Collections.ObjectModel;
using Gert.Console.Tools;
using Gert.Console.Tui.State;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Gert.Console.Tui.Dialogs;

/// <summary>
/// The tools menu (U16) — the console analog of <c>tools-menu.js</c>: toggle
/// each capability for the next turn, plus thinking, preserve-thinking, and
/// the approval mode (auto-apply). Enter/Space flips the selected row; Esc
/// closes. Mutates <see cref="ComposerState"/>/<see cref="IToolApprover"/>
/// directly — the next send carries exactly what this menu shows.
/// </summary>
public static class ToolsMenuDialog
{
    /// <summary>Show the menu modally.</summary>
    public static void Show(IApplication application, ComposerState composer, IToolApprover approver)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(approver);

        // Row model: label + read + flip.
        var rows = new List<(Func<string> Label, Action Flip)>();
        foreach (var id in composer.Tools.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var toolId = id;
            rows.Add((
                () => $"[{(composer.Tools[toolId] ? 'x' : ' ')}] {toolId}",
                () => composer.SetTool(toolId, !composer.Tools[toolId])));
        }

        rows.Add((
            () => $"[{(composer.Thinking ? 'x' : ' ')}] thinking",
            () => composer.Thinking = !composer.Thinking));
        rows.Add((
            () => $"[{(composer.PreserveThinking ? 'x' : ' ')}] preserve thinking",
            () => composer.PreserveThinking = !composer.PreserveThinking));
        rows.Add((
            () => $"[{(approver.AutoApprove ? 'x' : ' ')}] auto-apply edits (skip approval)",
            () => approver.AutoApprove = !approver.AutoApprove));

        var items = new ObservableCollection<string>(rows.Select(r => r.Label()));

        using var dialog = new Dialog
        {
            Title = "Tools — Enter toggles, Esc closes",
            Width = Dim.Percent(50),
            Height = Math.Min(rows.Count + 4, 20),
        };

        var list = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        list.SetSource(items);

        list.KeyDown += (_, key) =>
        {
            if (key == Key.Enter || key == Key.Space)
            {
                key.Handled = true;
                if (list.SelectedItem is { } index && index >= 0 && index < rows.Count)
                {
                    rows[index].Flip();
                    items[index] = rows[index].Label();
                }
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
    }
}
