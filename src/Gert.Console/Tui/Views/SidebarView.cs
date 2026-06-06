using System.Collections.ObjectModel;
using Gert.Console.Tui.State;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Gert.Console.Tui.Views;

/// <summary>
/// The sidebar pane (U16): active project header + the conversation list —
/// the console analog of <c>sidebar.js</c>. Enter opens, <c>n</c> news,
/// <c>r</c> renames, <c>d</c> deletes, <c>p</c> opens the project picker.
/// All data work happens in <see cref="SidebarPresenter"/>; this view only
/// projects it and raises intents.
/// </summary>
public sealed class SidebarView : FrameView
{
    private readonly SidebarPresenter _presenter;
    private readonly ListView _list;
    private readonly ObservableCollection<string> _titles = [];

    public SidebarView(SidebarPresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));

        Title = "Conversations";
        _list = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        _list.SetSource(_titles);

        var hint = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Text = "⏎ open n new r rename d del p proj",
        };

        Add(_list, hint);

        _presenter.Changed += OnPresenterChanged;
        _list.KeyDown += OnListKeyDown;
    }

    /// <summary>The user activated a conversation (Enter).</summary>
    public event Action<string>? ConversationActivated;

    /// <summary>The user asked for a new chat.</summary>
    public event Action? NewChatRequested;

    /// <summary>The user asked to rename the selected conversation.</summary>
    public event Action<string>? RenameRequested;

    /// <summary>The user asked to delete the selected conversation.</summary>
    public event Action<string>? DeleteRequested;

    /// <summary>The user asked for the project picker.</summary>
    public event Action? ProjectPickerRequested;

    private string? SelectedConversationId =>
        _list.SelectedItem is { } index && index >= 0 && index < _presenter.Conversations.Count
            ? _presenter.Conversations[index].Id
            : null;

    private void OnPresenterChanged()
    {
        var project = _presenter.Projects
            .FirstOrDefault(p => string.Equals(p.Id, _presenter.Pid, StringComparison.Ordinal));
        Title = project is null ? "Conversations" : $"Conversations — {project.Name}";

        _titles.Clear();
        foreach (var conversation in _presenter.Conversations)
        {
            var marker = string.Equals(conversation.Id, _presenter.SelectedConversationId, StringComparison.Ordinal)
                ? "● "
                : "  ";
            _titles.Add(marker + conversation.Title);
        }

        SetNeedsDraw();
    }

    private void OnListKeyDown(object? sender, Key key)
    {
        if (key == Key.Enter && SelectedConversationId is { } open)
        {
            key.Handled = true;
            ConversationActivated?.Invoke(open);
        }
        else if (key == Key.N)
        {
            key.Handled = true;
            NewChatRequested?.Invoke();
        }
        else if (key == Key.R && SelectedConversationId is { } rename)
        {
            key.Handled = true;
            RenameRequested?.Invoke(rename);
        }
        else if ((key == Key.D || key == Key.Delete) && SelectedConversationId is { } delete)
        {
            key.Handled = true;
            DeleteRequested?.Invoke(delete);
        }
        else if (key == Key.P)
        {
            key.Handled = true;
            ProjectPickerRequested?.Invoke();
        }
    }
}
