using Gert.Console.Tui.State;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Gert.Console.Tui.Views;

/// <summary>
/// The chat transcript pane (U16) — a custom-drawn view over
/// <see cref="ChatTranscript.Lines"/>: wraps to the viewport, colors by
/// <see cref="LineKind"/>, follows the stream until the user scrolls, and
/// toggles collapsible regions (thinking, tool cards) with Enter on the
/// selected header row. All content logic lives in the headless model; this
/// view only projects it.
/// </summary>
public sealed class TranscriptView : View
{
    private readonly ChatTranscript _transcript;
    private readonly List<DisplayRow> _rows = [];
    private int _scroll;
    private int _selected;
    private bool _follow = true;
    private bool _rowsDirty = true;
    private int _lastWidth;

    private readonly record struct DisplayRow(string Text, LineKind Kind, string? RegionId, bool IsHeader);

    public TranscriptView(ChatTranscript transcript)
    {
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        CanFocus = true;
        _transcript.Changed += OnTranscriptChanged;
        KeyDown += OnKeyDown;
    }

    private void OnTranscriptChanged()
    {
        _rowsDirty = true;
        SetNeedsDraw();
    }

    private void OnKeyDown(object? sender, Key key)
    {
        var page = Math.Max(1, Viewport.Height - 1);
        if (key == Key.CursorUp)
        {
            _follow = false;
            _selected = Math.Max(0, _selected - 1);
        }
        else if (key == Key.CursorDown)
        {
            _selected = Math.Min(Math.Max(0, _rows.Count - 1), _selected + 1);
        }
        else if (key == Key.PageUp)
        {
            _follow = false;
            _selected = Math.Max(0, _selected - page);
        }
        else if (key == Key.PageDown)
        {
            _selected = Math.Min(Math.Max(0, _rows.Count - 1), _selected + page);
        }
        else if (key == Key.Home)
        {
            _follow = false;
            _selected = 0;
        }
        else if (key == Key.End)
        {
            _follow = true;
            _selected = Math.Max(0, _rows.Count - 1);
        }
        else if (key == Key.Enter)
        {
            ToggleSelectedRegion();
        }
        else
        {
            return;
        }

        key.Handled = true;
        EnsureSelectedVisible();
        SetNeedsDraw();
    }

    private void ToggleSelectedRegion()
    {
        if (_selected >= 0 && _selected < _rows.Count && _rows[_selected] is { IsHeader: true, RegionId: { } regionId })
        {
            _transcript.ToggleRegion(regionId);
        }
    }

    private void EnsureSelectedVisible()
    {
        var height = Math.Max(1, Viewport.Height);
        if (_selected < _scroll)
        {
            _scroll = _selected;
        }
        else if (_selected >= _scroll + height)
        {
            _scroll = _selected - height + 1;
        }
    }

    /// <inheritdoc />
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Math.Max(1, Viewport.Width);
        var height = Math.Max(1, Viewport.Height);

        if (_rowsDirty || width != _lastWidth)
        {
            RebuildRows(width);
            _lastWidth = width;
            _rowsDirty = false;
        }

        if (_follow)
        {
            _scroll = Math.Max(0, _rows.Count - height);
            _selected = Math.Max(0, _rows.Count - 1);
        }

        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _rows.Count - 1));

        var normal = GetAttributeForRole(VisualRole.Normal);
        for (var row = 0; row < height; row++)
        {
            var index = _scroll + row;
            var attribute = normal;
            var text = string.Empty;
            if (index < _rows.Count)
            {
                var display = _rows[index];
                attribute = HasFocus && index == _selected
                    ? GetAttributeForRole(VisualRole.Focus)
                    : AttributeFor(display.Kind, normal);
                text = display.Text;
            }

            SetAttribute(attribute);
            Move(0, row);
            AddStr(text.Length > width ? text[..width] : text.PadRight(width));
        }

        return true;
    }

    private Attribute AttributeFor(LineKind kind, Attribute normal)
    {
        var background = normal.Background;
        return kind switch
        {
            LineKind.UserHeader => new Attribute(new Color(StandardColor.BrightCyan), background),
            LineKind.AssistantHeader => new Attribute(new Color(StandardColor.BrightGreen), background),
            LineKind.Heading => new Attribute(new Color(StandardColor.BrightYellow), background),
            LineKind.Code => new Attribute(new Color(StandardColor.GreenPhosphor), background),
            LineKind.ThinkingHeader or LineKind.Thinking or LineKind.Meta =>
                new Attribute(new Color(StandardColor.Gray), background),
            LineKind.ToolHeader => new Attribute(new Color(StandardColor.SkyBlue), background),
            LineKind.ToolBody => new Attribute(new Color(StandardColor.LightSlateGray), background),
            LineKind.Citation => new Attribute(new Color(StandardColor.SteelBlue), background),
            LineKind.Error => new Attribute(new Color(StandardColor.BrightRed), background),
            _ => normal,
        };
    }

    private void RebuildRows(int width)
    {
        _rows.Clear();
        foreach (var line in _transcript.Lines())
        {
            if (line.Text.Length <= width)
            {
                _rows.Add(new DisplayRow(line.Text, line.Kind, line.RegionId, line.IsRegionHeader));
                continue;
            }

            // Wrap long lines into width-sized rows; only the first carries the
            // header marker (Enter targets it).
            var first = true;
            for (var offset = 0; offset < line.Text.Length; offset += width)
            {
                var chunk = line.Text.Substring(offset, Math.Min(width, line.Text.Length - offset));
                _rows.Add(new DisplayRow(chunk, line.Kind, line.RegionId, first && line.IsRegionHeader));
                first = false;
            }
        }
    }
}
