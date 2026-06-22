using Gert.Model.Agent;

namespace Gert.Agent.Loop;

/// <summary>An <see cref="IAgentEventSink"/> that forwards each event to a delegate (the driver's tee handler).</summary>
public sealed class DelegatingAgentEventSink(Func<AgentEvent, CancellationToken, ValueTask> onEvent) : IAgentEventSink
{
    private readonly Func<AgentEvent, CancellationToken, ValueTask> _onEvent =
        onEvent ?? throw new ArgumentNullException(nameof(onEvent));

    public ValueTask EmitAsync(AgentEvent ev, CancellationToken cancellationToken) => _onEvent(ev, cancellationToken);
}
