namespace Gert.Console.Tools;

/// <summary>
/// The TUI-backed <see cref="IToolApprover"/> (U16): gated tools call
/// <see cref="RequestAsync"/> from the turn runner's worker thread; the
/// attached <see cref="Handler"/> (set by the TUI shell at startup) marshals
/// the approval dialog onto the UI loop and completes with the user's verdict.
/// No Terminal.Gui types here — the marshal lives inside the handler, keeping
/// this (and the tools) headless-testable. Fail-safe: without a handler,
/// gated actions are denied.
/// </summary>
public sealed class TuiToolApprover : IToolApprover
{
    /// <inheritdoc />
    public bool AutoApprove { get; set; }

    /// <summary>
    /// The UI bridge: shows the approval dialog and resolves with the decision.
    /// Cancellation (turn stop) must propagate out of the returned task.
    /// </summary>
    public Func<ApprovalRequest, CancellationToken, Task<ApprovalDecision>>? Handler { get; set; }

    /// <inheritdoc />
    public async Task<ApprovalDecision> RequestAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (AutoApprove)
        {
            return ApprovalDecision.Approve;
        }

        var handler = Handler;
        if (handler is null)
        {
            // No UI attached to ask — deny rather than silently write.
            return ApprovalDecision.Deny;
        }

        return await handler(request, cancellationToken).ConfigureAwait(false);
    }
}
