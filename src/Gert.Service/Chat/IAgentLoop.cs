namespace Gert.Service.Chat;

/// <summary>
/// The reusable tool loop (chat-and-tools.md section the tool loop), detached from
/// any transport AND any persistence: it streams the model, coalesces deltas,
/// enforces the round + search budgets, re-checks per-call entitlement, executes
/// tools through the host, and feeds results back upstream - talking ONLY through
/// the request's callbacks (<see cref="AgentLoopRequest.Emit"/>,
/// <see cref="AgentLoopRequest.OnToolExecuted"/>, <see cref="AgentLoopRequest.OnProgress"/>),
/// the host, and the model client. The driver (the chat shell, the sub-agent, a
/// headless run) owns message_start/message_end, citation persistence, and the
/// terminal finalize.
/// </summary>
public interface IAgentLoop
{
    /// <summary>Run the loop to its final answer, returning the accumulated content/reasoning/metrics.</summary>
    Task<AgentLoopResult> RunAsync(AgentLoopRequest request, CancellationToken cancellationToken = default);
}
