namespace Gert.Console.Tools;

/// <summary>The no-op <see cref="IWorkspaceObserver"/> for headless hosts (tests, CLI).</summary>
public sealed class NullWorkspaceObserver : IWorkspaceObserver
{
    /// <inheritdoc />
    public void OnEditApplied(ApprovalRequest request)
    {
        // Nothing to record without a workspace pane.
    }
}
