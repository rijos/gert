namespace Gert.Tools;

/// <summary>
/// Delegation to a nested agent loop (chat-and-tools.md section sub-agent) - the seam the
/// <c>run_sub_agent</c> tool drives so it depends on a contract, not the loop impl. The chat
/// driver supplies a <c>ChatToolDelegate</c> over the same <c>IAgentLoop</c> the turn runs;
/// an autonomous host (the sub-agent's own nested loop) wires a no-op, so delegation never recurses.
/// </summary>
public interface IToolDelegate
{
    /// <summary>Run the delegated task to completion and return only its final text.</summary>
    Task<DelegateResult> RunAsync(DelegateRequest request, CancellationToken cancellationToken = default);
}
