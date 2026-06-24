using Gert.Model.Agent;

namespace Gert.Agent.Loop;

/// <summary>
/// The discard sink for an autonomous run (the sub-agent / a headless driver): the loop emits
/// nothing observable and only its returned <see cref="AgentResult"/> matters.
/// </summary>
public sealed class NullAgentEventSink : IAgentEventSink
{
    public static readonly NullAgentEventSink Instance = new();

    private NullAgentEventSink()
    {
    }

    public ValueTask EmitAsync(AgentEvent ev, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
