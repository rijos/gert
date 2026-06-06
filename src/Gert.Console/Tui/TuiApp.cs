using Gert.Console.Tools;
using Gert.Console.Tui.Dialogs;
using Gert.Console.Tui.State;
using Gert.Console.Tui.Views;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Service.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Gert.Console.Tui;

/// <summary>
/// The TUI shell window — the console analog of the SPA's
/// <c>app-shell.js</c> three-pane layout: sidebar (conversations) | chat
/// (transcript + composer) | workspace (touched files), over a one-line
/// status bar. Owns the presenters and the UI marshal
/// (<c>IApplication.Invoke</c>); panes fill in across U16.8–11.
/// </summary>
public sealed class TuiApp : Window
{
    private readonly IApplication _application;
    private readonly IServiceProvider _services;
    private readonly ILogger<TuiApp> _logger;

    private readonly ChatTranscript _transcript;
    private readonly ChatPresenter _presenter;
    private readonly ComposerState _composerState;
    private readonly WorkspacePresenter _workspace;
    private readonly TuiToolApprover _approver;
    private readonly SidebarPresenter _sidebar;
    private readonly SettingsPresenter _settings;
    private readonly KnowledgePresenter _knowledge;

    private readonly SidebarView _sidebarPane;
    private readonly TranscriptView _transcriptView;
    private readonly ComposerView _composerView;
    private readonly WorkspaceView _workspacePane;
    private readonly StatusBarView _statusBar;

    private const int SidebarWidth = 30;
    private const int WorkspaceWidth = 42;
    private const int ComposerHeight = 5;

    /// <summary>Build the shell over the configured root provider.</summary>
    public TuiApp(IApplication application, IServiceProvider services)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = services.GetRequiredService<ILogger<TuiApp>>();

        Title = "Gert";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        Action<Action> uiInvoke = action => _application.Invoke(action);

        // Headless state + presenters (the actual application).
        _transcript = new ChatTranscript();
        _presenter = new ChatPresenter(_services, _transcript, uiInvoke);
        _composerState = new ComposerState();
        _composerState.SeedTools(_services.GetRequiredService<ToolRegistry>().AllIds);
        _workspace = _services.GetRequiredService<WorkspacePresenter>();
        _workspace.UiInvoke = uiInvoke;
        _approver = _services.GetRequiredService<TuiToolApprover>();
        _sidebar = new SidebarPresenter(_services, uiInvoke);
        _settings = new SettingsPresenter(_services);
        _knowledge = new KnowledgePresenter(_services);

        // Panes.
        _sidebarPane = new SidebarView(_sidebar)
        {
            X = 0,
            Y = 0,
            Width = SidebarWidth,
            Height = Dim.Fill(1),
        };

        _transcriptView = new TranscriptView(_transcript)
        {
            X = Pos.Right(_sidebarPane),
            Y = 0,
            Width = Dim.Fill(WorkspaceWidth),
            Height = Dim.Fill(ComposerHeight + 1),
        };

        _composerView = new ComposerView
        {
            X = Pos.Right(_sidebarPane),
            Y = Pos.AnchorEnd(ComposerHeight + 1),
            Width = Dim.Fill(WorkspaceWidth),
            Height = ComposerHeight,
        };

        _workspacePane = new WorkspaceView(_workspace)
        {
            X = Pos.AnchorEnd(WorkspaceWidth),
            Y = 0,
            Width = WorkspaceWidth,
            Height = Dim.Fill(1),
        };

        _statusBar = new StatusBarView
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
        };

        Add(_sidebarPane, _transcriptView, _composerView, _workspacePane, _statusBar);

        // The approval bridge: gated tools block on this from the worker thread;
        // the dialog runs modally on the UI loop.
        _approver.Handler = RequestApprovalAsync;

        _composerView.SendRequested += OnSendRequested;
        _transcript.Changed += UpdateStatusBar;
        _presenter.ConversationCreated += OnConversationCreated;
        _sidebarPane.ConversationActivated += OnConversationActivated;
        _sidebarPane.NewChatRequested += NewChat;
        _sidebarPane.RenameRequested += OnRenameRequested;
        _sidebarPane.DeleteRequested += OnDeleteRequested;
        _sidebarPane.ProjectPickerRequested += OnProjectPickerRequested;
        KeyDown += OnShellKeyDown;
        UpdateStatusBar();
        _composerView.FocusInput();

        Fire(_sidebar.RefreshAsync(), "sidebar refresh");
    }

    /// <summary>
    /// The <see cref="TuiToolApprover.Handler"/>: marshal the approval dialog
    /// onto the UI loop and resolve with the verdict. Never blocks the UI
    /// thread on the tool; never lets a no-answer apply anything.
    /// </summary>
    private Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _application.Invoke(() =>
        {
            try
            {
                var result = ApprovalDialog.Show(_application, request, cancellationToken);
                if (result is null)
                {
                    // The turn was stopped while the dialog was open.
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                if (result.AutoApproveAll)
                {
                    _approver.AutoApprove = true;
                    UpdateStatusBar();
                }

                tcs.TrySetResult(result.Decision);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    /// <summary>Fire-and-forget an async intent; faults go to the NDJSON log.</summary>
    private void Fire(Task task, string what) =>
        task.ContinueWith(
            t => _logger.LogError(t.Exception, "{What} faulted", what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private void OnConversationCreated(string conversationId)
    {
        _sidebar.SelectedConversationId = conversationId;
        Fire(_sidebar.RefreshAsync(), "sidebar refresh");
    }

    private void OnConversationActivated(string conversationId)
    {
        if (_transcript.Streaming)
        {
            return;
        }

        Fire(OpenConversationAsync(conversationId), "open conversation");
    }

    private async Task OpenConversationAsync(string conversationId)
    {
        var thread = await _sidebar.OpenAsync(conversationId).ConfigureAwait(false);
        if (thread is null)
        {
            return;
        }

        _application.Invoke(() =>
        {
            _presenter.Attach(conversationId);
            _transcript.Rebuild(thread);
            _composerView.FocusInput();
        });

        // Re-attach to an in-flight turn: the thread's last assistant row is
        // still streaming — replay its events from the row's seq and tail live
        // (detached generation: switching conversations mid-turn loses nothing).
        var last = thread.Messages.Count > 0 ? thread.Messages[^1] : null;
        if (last is { Role: MessageRole.Assistant, Status: MessageStatus.Streaming })
        {
            await _presenter.ResumeAsync(last.Seq).ConfigureAwait(false);
        }
    }

    private void NewChat()
    {
        if (_transcript.Streaming)
        {
            return;
        }

        _presenter.NewConversation();
        _sidebar.SelectedConversationId = null;
        _composerView.FocusInput();
    }

    private void OnRenameRequested(string conversationId)
    {
        var current = _sidebar.Conversations
            .FirstOrDefault(c => string.Equals(c.Id, conversationId, StringComparison.Ordinal))?.Title ?? string.Empty;
        if (InputDialog.Show(_application, "Rename conversation", "Title:", current) is { } title)
        {
            Fire(_sidebar.RenameAsync(conversationId, title), "rename conversation");
        }
    }

    private void OnDeleteRequested(string conversationId)
    {
        if (_transcript.Streaming)
        {
            return;
        }

        var wasOpen = string.Equals(_presenter.ConversationId, conversationId, StringComparison.Ordinal);
        Fire(_sidebar.DeleteAsync(conversationId), "delete conversation");
        if (wasOpen)
        {
            _presenter.NewConversation();
        }
    }

    private void OnProjectPickerRequested()
    {
        var names = _sidebar.Projects.Select(p => p.Name).Append("+ New project…").ToList();
        var current = _sidebar.Projects.ToList()
            .FindIndex(p => string.Equals(p.Id, _sidebar.Pid, StringComparison.Ordinal));
        var choice = ListPickerDialog.Show(_application, "Project", names, Math.Max(0, current));
        if (choice is not { } index)
        {
            return;
        }

        if (index == names.Count - 1)
        {
            if (InputDialog.Show(_application, "New project", "Name:") is { } name)
            {
                _presenter.NewConversation();
                Fire(_sidebar.CreateProjectAsync(name).ContinueWith(
                    _ => _application.Invoke(() => _presenter.Pid = _sidebar.Pid),
                    TaskScheduler.Default), "create project");
            }

            return;
        }

        var pid = _sidebar.Projects[index].Id;
        if (!string.Equals(pid, _sidebar.Pid, StringComparison.Ordinal))
        {
            _presenter.NewConversation();
            Fire(_sidebar.SwitchProjectAsync(pid).ContinueWith(
                _ => _application.Invoke(() => _presenter.Pid = _sidebar.Pid),
                TaskScheduler.Default), "switch project");
        }
    }

    private void OnSendRequested(string text)
    {
        if (_transcript.Streaming)
        {
            return;
        }

        // Fire-and-forget: the presenter marshals every transcript mutation back
        // onto this UI loop; faults land in the transcript as error entries.
        Fire(_presenter.SendAsync(text, _composerState), "send turn");
        Fire(RefreshSidebarAfterSend(), "sidebar refresh");
    }

    /// <summary>Auto-titles materialize on the first message — refresh shortly after.</summary>
    private async Task RefreshSidebarAfterSend()
    {
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await _sidebar.RefreshAsync().ConfigureAwait(false);
    }

    private void OnShellKeyDown(object? sender, Key key)
    {
        if (key == Key.Q.WithCtrl)
        {
            key.Handled = true;
            _application.RequestStop(this);
        }
        else if (key == Key.Esc && _transcript.Streaming)
        {
            key.Handled = true;
            _presenter.Stop();
        }
        else if (key == Key.N.WithCtrl)
        {
            key.Handled = true;
            NewChat();
        }
        else if (key == Key.P.WithCtrl)
        {
            key.Handled = true;
            OnProjectPickerRequested();
        }
        else if (key == Key.M.WithCtrl)
        {
            key.Handled = true;
            ShowModelPicker();
        }
        else if (key == Key.T.WithCtrl)
        {
            key.Handled = true;
            ToolsMenuDialog.Show(_application, _composerState, _approver);
            UpdateStatusBar();
        }
        else if (key == Key.K.WithCtrl)
        {
            key.Handled = true;
            _knowledge.Pid = _sidebar.Pid;
            KnowledgeDialog.Show(_application, _knowledge);
        }
        else if (key == Key.S.WithCtrl)
        {
            key.Handled = true;
            ShowSettings();
        }
    }

    private void ShowModelPicker()
    {
        var models = _services.GetRequiredService<Service.External.IModelCatalog>().List();
        if (models.Count == 0)
        {
            MessageDialog.Show(_application, "Models", "No model catalog configured — using the server default.");
            return;
        }

        var names = models
            .Select(m => $"{m.Name}{(m.Context is { } c ? $" · {c / 1000}K ctx" : string.Empty)}{(m.Default ? " (default)" : string.Empty)}")
            .ToList();
        var current = models.ToList()
            .FindIndex(m => string.Equals(m.Id, _composerState.ModelId, StringComparison.Ordinal));
        if (ListPickerDialog.Show(_application, "Model", names, Math.Max(0, current)) is { } index)
        {
            _composerState.ModelId = models[index].Id;
            UpdateStatusBar();
        }
    }

    private void ShowSettings()
    {
        Fire(ShowSettingsAsync(), "settings");
    }

    private async Task ShowSettingsAsync()
    {
        var current = await _settings.LoadAsync().ConfigureAwait(false);
        _application.Invoke(() =>
        {
            if (SettingsDialog.Show(_application, current) is { } update)
            {
                Fire(_settings.SaveAsync(update), "save settings");
            }
        });
    }

    private void UpdateStatusBar()
    {
        var left = "^Q quit ^N new ^P proj ^M model ^T tools ^K know ^S setting";
        var model = _composerState.ModelId ?? "default";
        var mode = _approver.AutoApprove ? "auto-apply" : "approve-each";
        var usage = _transcript.Usage;
        var right = usage is null
            ? $"{model} · {mode}"
            : $"{model} · {usage.Display()} · {mode}";
        _statusBar.Set(left, right, usage?.Band ?? 0);
    }
}
