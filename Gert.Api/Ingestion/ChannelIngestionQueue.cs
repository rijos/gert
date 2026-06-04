using System.Threading.Channels;
using Gert.Service.Ingestion;

namespace Gert.Api.Ingestion;

/// <summary>
/// The production <see cref="IIngestionQueue"/> (U9b) — a
/// <c>System.Threading.Channels</c>-backed queue drained by
/// <see cref="IngestionWorker"/>. <see cref="EnqueueAsync"/> returns as soon as the
/// job is accepted (it is unbounded, so it never blocks the request), so an upload
/// responds <c>202</c> immediately while extract → chunk → embed → write runs
/// off-thread. Registered as a singleton, shared between the document service
/// (writer) and the background worker (reader).
/// </summary>
public sealed class ChannelIngestionQueue : IIngestionQueue
{
    private readonly Channel<IngestJob> _channel =
        Channel.CreateUnbounded<IngestJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>The reader the worker drains.</summary>
    public ChannelReader<IngestJob> Reader => _channel.Reader;

    /// <inheritdoc />
    public async Task EnqueueAsync(IngestJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await _channel.Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
    }
}
