using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Gert.Console.Tui.Views;

/// <summary>
/// Renders a unified diff with +/− coloring (U16) — the body of the
/// ApprovalDialog and the workspace pane's detail view. Scrollable; content
/// is just the precomputed diff string from <c>UnifiedDiff</c>.
/// </summary>
public sealed class DiffView : View
{
    private string[] _lines = [];
    private int _scroll;

    public DiffView()
    {
        CanFocus = true;
        KeyDown += OnKeyDown;
    }

    /// <summary>Replace the displayed diff (null clears).</summary>
    public void SetDiff(string? diff)
    {
        _lines = string.IsNullOrEmpty(diff) ? [] : diff.TrimEnd('\n').Split('\n');
        _scroll = 0;
        SetNeedsDraw();
    }

    private void OnKeyDown(object? sender, Key key)
    {
        var page = Math.Max(1, Viewport.Height - 1);
        var max = Math.Max(0, _lines.Length - Math.Max(1, Viewport.Height));
        if (key == Key.CursorUp)
        {
            _scroll = Math.Max(0, _scroll - 1);
        }
        else if (key == Key.CursorDown)
        {
            _scroll = Math.Min(max, _scroll + 1);
        }
        else if (key == Key.PageUp)
        {
            _scroll = Math.Max(0, _scroll - page);
        }
        else if (key == Key.PageDown)
        {
            _scroll = Math.Min(max, _scroll + page);
        }
        else
        {
            return;
        }

        key.Handled = true;
        SetNeedsDraw();
    }

    /// <inheritdoc />
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Math.Max(1, Viewport.Width);
        var height = Math.Max(1, Viewport.Height);
        var normal = GetAttributeForRole(VisualRole.Normal);

        for (var row = 0; row < height; row++)
        {
            var index = _scroll + row;
            var text = index < _lines.Length ? _lines[index] : string.Empty;
            SetAttribute(AttributeFor(text, normal));
            Move(0, row);
            AddStr(text.Length > width ? text[..width] : text.PadRight(width));
        }

        return true;
    }

    private static Attribute AttributeFor(string line, Attribute normal) => line switch
    {
        ['+', ..] when !line.StartsWith("+++", StringComparison.Ordinal) =>
            new Attribute(new Color(StandardColor.BrightGreen), normal.Background),
        ['-', ..] when !line.StartsWith("---", StringComparison.Ordinal) =>
            new Attribute(new Color(StandardColor.BrightRed), normal.Background),
        ['@', '@', ..] => new Attribute(new Color(StandardColor.BrightCyan), normal.Background),
        _ => new Attribute(new Color(StandardColor.Gray), normal.Background),
    };
}
