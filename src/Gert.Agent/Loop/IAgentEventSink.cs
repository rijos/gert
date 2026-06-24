using Gert.Model.Agent;

namespace Gert.Agent.Loop;

/// <summary>
/// The agent loop's single output (refactor: split the noun - one sink replaces the five
/// driver callbacks). The loop emits every <see cref="AgentEvent"/> through here and knows
/// nothing of what is downstream: an event-log tee (the chat driver), a channel bridge
/// (<c>ChannelSink</c> behind <c>IAgent</c>), or a discard (the autonomous sub-agent). Cheap
/// producer, nice consumer.
/// </summary>
public interface IAgentEventSink
{
    ValueTask EmitAsync(AgentEvent ev, CancellationToken cancellationToken);
}
