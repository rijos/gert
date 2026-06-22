using Gert.Model.Agent;

namespace Gert.Agent.Loop;

/// <summary>
/// The reusable tool loop (chat-and-tools.md section the tool loop), detached from any transport
/// AND any persistence: it streams the model, enforces the round + per-tool budgets, re-checks
/// per-call entitlement, executes tools through the host, and feeds results back upstream - emitting
/// every observable step as an <see cref="AgentEvent"/> through the one <see cref="IAgentEventSink"/>.
/// It knows nothing of logs, buses, conversations, or coalescing. The consumer (the chat driver's
/// event-log tee, the sub-agent's discard) maps the events to whatever it needs; the driver owns
/// message_start/message_end, citation persistence, and the terminal finalize.
/// </summary>
public interface IAgentLoop
{
    /// <summary>Run the loop to its final answer, emitting through <paramref name="sink"/> and returning the metrics.</summary>
    Task<AgentResult> RunAsync(
        AgentLoopRequest request,
        IAgentEventSink sink,
        CancellationToken cancellationToken = default);
}
