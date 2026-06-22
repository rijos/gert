using Gert.Agent.Loop;
using Gert.Model.Agent;

namespace Gert.Agent;

/// <summary>
/// The agent - compute, in your name, in the background (refactor: split the noun). Process-local:
/// it owns its background task (the agent number) and an <see cref="AgentEvent"/> stream out, and
/// knows nothing of logs, buses, conversations, HTTP, or resumption. <see cref="Start"/> runs the
/// reusable loop on a background task behind a channel; the caller reads <see cref="IAgentRun.Events"/>
/// while it's busy and tees them into the durable conversation event log. Sink inside, stream out.
/// </summary>
public interface IAgent
{
    /// <summary>Run this request in my name, in the background; return the live handle to its events + completion.</summary>
    IAgentRun Start(AgentLoopRequest request, CancellationToken cancellationToken = default);
}
