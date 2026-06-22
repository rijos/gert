using System.Threading.Channels;
using Gert.Agent.Loop;
using Gert.Model.Agent;

namespace Gert.Agent;

/// <summary>The bridge: the loop's <see cref="IAgentEventSink"/> writes each event into the channel the tee reads.</summary>
internal sealed class ChannelSink(ChannelWriter<AgentEvent> writer) : IAgentEventSink
{
    public ValueTask EmitAsync(AgentEvent ev, CancellationToken cancellationToken) =>
        writer.WriteAsync(ev, cancellationToken);
}
