using System.Collections.ObjectModel;
using Gert.Console.Tui.State;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Gert.Console.Tui.Views;

/// <summary>
/// The right pane (U16) — where the web shows the artifact canvas, the
/// console shows the LOCAL WORKSPACE: files the model touched this session
/// (top) and the selected file's last applied diff (bottom).
/// </summary>
public sealed class WorkspaceView : FrameView
{
    private readonly WorkspacePresenter _presenter;
    private readonly ListView _list;
    private readonly DiffView _diff;
    private readonly ObservableCollection<string> _rows = [];

    public WorkspaceView(WorkspacePresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));

        Title = "Workspace";
        _list = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(40),
        };
        _list.SetSource(_rows);

        _diff = new DiffView
        {
            X = 0,
            Y = Pos.Bottom(_list),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        Add(_list, _diff);

        _presenter.Changed += OnPresenterChanged;
        _list.ValueChanged += (_, _) => ShowSelectedDiff();
    }

    private void OnPresenterChanged()
    {
        _rows.Clear();
        foreach (var file in _presenter.Files)
        {
            var edits = file.Edits > 1 ? $" ({file.Edits})" : string.Empty;
            _rows.Add($"{file.Path}{edits}");
        }

        Title = _presenter.Files.Count == 0
            ? "Workspace"
            : $"Workspace — {_presenter.Files.Count} touched";

        if (_rows.Count > 0 && _list.SelectedItem is null)
        {
            _list.SelectedItem = 0;
        }

        ShowSelectedDiff();
        SetNeedsDraw();
    }

    private void ShowSelectedDiff()
    {
        var index = _list.SelectedItem ?? -1;
        _diff.SetDiff(index >= 0 && index < _presenter.Files.Count
            ? _presenter.Files[index].Diff
            : null);
    }
}
