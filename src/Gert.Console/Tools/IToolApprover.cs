namespace Gert.Console.Tools;

/// <summary>
/// The approval seam between the gated local tools and the TUI (U16). The
/// write/edit/shell tools block on <see cref="RequestAsync"/> from the turn
/// runner's worker scope; the TUI implementation marshals a dialog onto the UI
/// loop and completes when the user decides. Lives in <c>Gert.Console</c> —
/// <c>Gert.Service</c> never learns approval exists (the gate runs inside the
/// tool's own <c>ExecuteAsync</c>).
/// </summary>
public interface IToolApprover
{
    /// <summary>
    /// When true, gated actions apply immediately without asking (the tools
    /// menu's "auto-apply" toggle). Default is approve-each.
    /// </summary>
    bool AutoApprove { get; set; }

    /// <summary>
    /// Ask the user to approve <paramref name="request"/>. Cancellation (the
    /// turn was stopped) propagates as <see cref="OperationCanceledException"/>,
    /// which the runner converts to the turn's <c>cancelled</c> finalize.
    /// </summary>
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken = default);
}
