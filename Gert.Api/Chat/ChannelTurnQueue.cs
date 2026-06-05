using System.Threading.Channels;
using Gert.Service.Chat;

namespace Gert.Api.Chat;

/// <summary>
/// The production <see cref="ITurnQueue"/> — a Channel-backed queue drained by
/// <see cref="TurnWorker"/> (chat-and-tools.md § detached turns; mirrors
/// <see cref="Ingestion.ChannelIngestionQueue"/>). Unbounded, so POST never
/// blocks on enqueue; in-memory and non-durable by design — the orphan rule
/// covers jobs lost to a crash. Singleton, shared between the message controller
/// (writer) and the worker (reader).
/// </summary>
public sealed class ChannelTurnQueue : ITurnQueue
{
    private readonly Channel<TurnJob> _channel =
        Channel.CreateUnbounded<TurnJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>The reader the worker drains.</summary>
    public ChannelReader<TurnJob> Reader => _channel.Reader;

    /// <inheritdoc />
    public async Task EnqueueAsync(TurnJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await _channel.Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
    }
}
