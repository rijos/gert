using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Gert.Console.Tui.Views;

/// <summary>
/// The message composer (U16) — the console analog of <c>composer.js</c>: a
/// multiline input; Ctrl+Enter sends, Esc bubbles up to the shell (stop).
/// Send-time options (model, tools, thinking) live in
/// <see cref="State.ComposerState"/> behind the shell's dialogs.
/// </summary>
public sealed class ComposerView : FrameView
{
    // TextView is marked obsolete in favor of the external gui-cs/Editor
    // package — a full code editor we don't need for a 5-line composer; the
    // widget itself remains functional in 2.4.x.
#pragma warning disable CS0618
    private readonly TextView _input;

    public ComposerView()
    {
        Title = "Message — Ctrl+Enter to send";
        _input = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            WordWrap = true,
        };
        Add(_input);
        _input.KeyDown += OnInputKeyDown;
    }
#pragma warning restore CS0618

    /// <summary>Raised with the trimmed message when the user sends.</summary>
    public event Action<string>? SendRequested;

    /// <summary>Focus the input (the shell's default focus target).</summary>
    public void FocusInput() => _input.SetFocus();

    private void OnInputKeyDown(object? sender, Key key)
    {
        if (key != Key.Enter.WithCtrl)
        {
            return;
        }

        key.Handled = true;
        var text = _input.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        _input.Text = string.Empty;
        SendRequested?.Invoke(text);
    }
}
