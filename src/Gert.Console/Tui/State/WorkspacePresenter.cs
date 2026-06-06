using Gert.Console.Tools;

namespace Gert.Console.Tui.State;

/// <summary>
/// The right pane's model (U16) — where the web has the artifact canvas, the
/// console shows the local workspace: every file the model touched this
/// session, with the latest applied diff per file. Implements
/// <see cref="IWorkspaceObserver"/>; the write tools call it from the worker
/// thread, so mutations go through the injected UI marshal.
/// </summary>
public sealed class WorkspacePresenter : IWorkspaceObserver
{
    private readonly List<TouchedFile> _files = [];

    /// <summary>
    /// The UI marshal — <c>Application.Invoke</c> once the TUI shell attaches
    /// it (the presenter is a DI singleton built before the UI loop exists);
    /// run-inline until then and in tests.
    /// </summary>
    public Action<Action> UiInvoke { get; set; } = action => action();

    /// <summary>Raised (on the UI thread) when the touched-files list changes.</summary>
    public event Action? Changed;

    /// <summary>Touched files, most recently edited first.</summary>
    public IReadOnlyList<TouchedFile> Files => _files;

    /// <inheritdoc />
    public void OnEditApplied(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        UiInvoke(() =>
        {
            var existing = _files.FirstOrDefault(f =>
                string.Equals(f.Path, request.Path, StringComparison.Ordinal));
            var edits = (existing?.Edits ?? 0) + 1;
            if (existing is not null)
            {
                _files.Remove(existing);
            }

            _files.Insert(0, new TouchedFile
            {
                Path = request.Path,
                Kind = request.Kind,
                Diff = request.UnifiedDiff,
                Edits = edits,
            });
            Changed?.Invoke();
        });
    }

    /// <summary>Forget the session's edit history (new chat does NOT clear it —
    /// the disk state persists; this is for an explicit user action).</summary>
    public void Clear()
    {
        UiInvoke(() =>
        {
            _files.Clear();
            Changed?.Invoke();
        });
    }
}
