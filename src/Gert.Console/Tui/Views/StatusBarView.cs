using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Gert.Console.Tui.Views;

/// <summary>
//// The one-line status bar (U16): key hints on the left; model · context
/// usage (the ring equivalent) · approval mode on the right. The shell feeds
/// both sides; amber/red context bands recolor the right segment like the
/// web ring.
/// </summary>
public sealed class StatusBarView : View
{
    private string _left = string.Empty;
    private string _right = string.Empty;
    private int _band;

    public StatusBarView()
    {
        Height = Dim.Absolute(1);
        Width = Dim.Fill();
        CanFocus = false;
    }

    /// <summary>Replace both segments (band: 0 ok, 1 amber, 2 red).</summary>
    public void Set(string left, string right, int band = 0)
    {
        _left = left ?? string.Empty;
        _right = right ?? string.Empty;
        _band = band;
        SetNeedsDraw();
    }

    /// <inheritdoc />
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Math.Max(1, Viewport.Width);
        var normal = GetAttributeForRole(VisualRole.Normal);
        var dim = new Attribute(new Color(StandardColor.Gray), normal.Background);

        SetAttribute(dim);
        Move(0, 0);
        AddStr(new string(' ', width));
        Move(0, 0);
        AddStr(_left.Length > width ? _left[..width] : _left);

        var rightAttr = _band switch
        {
            2 => new Attribute(new Color(StandardColor.BrightRed), normal.Background),
            1 => new Attribute(new Color(StandardColor.Orange), normal.Background),
            _ => dim,
        };
        var start = width - _right.Length;
        if (start > _left.Length + 1)
        {
            SetAttribute(rightAttr);
            Move(start, 0);
            AddStr(_right);
        }

        return true;
    }
}
