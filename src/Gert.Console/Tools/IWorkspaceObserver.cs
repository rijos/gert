namespace Gert.Console.Tools;

/// <summary>
/// How the TUI's workspace pane learns about applied edits (U16): the write
/// tools call <see cref="OnEditApplied"/> after a write lands on disk. The
/// pane implementation records touched files + diffs; headless hosts use the
/// no-op default.
/// </summary>
public interface IWorkspaceObserver
{
    /// <summary>An approved file edit was written to disk.</summary>
    void OnEditApplied(ApprovalRequest request);
}
