namespace Gert.Console.Tools;

/// <summary>
/// The default <see cref="IToolApprover"/>: every gated action is approved
/// immediately. Used headless (tests, future scripted mode); the TUI replaces
/// it with the dialog-backed approver at bootstrap.
/// </summary>
public sealed class AutoApproveApprover : IToolApprover
{
    /// <inheritdoc />
    public bool AutoApprove { get; set; } = true;

    /// <inheritdoc />
    public Task<ApprovalDecision> RequestAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ApprovalDecision.Approve);
    }
}
