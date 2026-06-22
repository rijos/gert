using System.Threading.Channels;
using Gert.Agent.Loop;
using Gert.Model.Agent;

namespace Gert.Agent;

/// <summary>
/// <see cref="IAgent"/> over the reusable <see cref="IAgentLoop"/>: <see cref="Start"/> spawns the
/// loop on a background task (the agent number names it) writing every <see cref="AgentEvent"/> into a
/// channel through a <see cref="ChannelSink"/>; the caller reads the bridged
/// <see cref="IAsyncEnumerable{T}"/>. Cheap producer (one sink write), nice consumer (a stream). The
/// channel is unbounded - the loop never blocks on a slow tee, and the tee's only back-pressure is its
/// own DB writes. Stateless beyond the counter, so a process-wide singleton.
/// </summary>
public sealed class Agent : IAgent
{
    private readonly IAgentLoop _loop;
    private int _counter;

    public Agent(IAgentLoop loop) => _loop = loop ?? throw new ArgumentNullException(nameof(loop));

    /// <inheritdoc />
    public IAgentRun Start(AgentLoopRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,  // the one tee reads
            SingleWriter = true,  // the one loop task writes through the sink
        });

        var number = Interlocked.Increment(ref _counter);
        var sink = new ChannelSink(channel.Writer);

        // The background task the number names. The finally completes the channel so the tee's
        // foreach ends on success AND on fault/cancel; the fault then surfaces via Completion.
        var completion = Task.Run(
            async () =>
            {
                try
                {
                    return await _loop.RunAsync(request, sink, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        return new AgentRun(number, channel.Reader, completion);
    }
}
