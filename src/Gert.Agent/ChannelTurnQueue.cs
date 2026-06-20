using System.Threading.Channels;
using Gert.Service.Chat;
using Microsoft.Extensions.Options;

namespace Gert.Agent;

/// <summary>
/// The production <see cref="ITurnQueue"/> - keyed lanes drained by
/// <see cref="TurnWorker"/> (chat-and-tools.md section detached turns; decisions section 11).
/// <see cref="TurnOptions.MaxConcurrentTurns"/> internal shards, each an
/// unbounded Channel: a job lands on the shard its <see cref="TurnKey"/> hashes
/// to, so one conversation's turns ride one lane in strict FIFO while different
/// conversations may run concurrently. Unbounded, so POST never blocks on
/// enqueue; in-memory and non-durable by design - the orphan rule covers jobs
/// lost to a crash. Singleton, shared between the message controller (writer)
/// and the worker (one reader loop per shard).
/// </summary>
public sealed class ChannelTurnQueue : ITurnQueue
{
    private readonly Channel<TurnJob>[] _shards;

    public ChannelTurnQueue(IOptions<TurnOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Host options validation enforces >= 1; the Max is belt-and-braces for
        // direct construction (tests).
        var lanes = Math.Max(1, options.Value.MaxConcurrentTurns);
        _shards = new Channel<TurnJob>[lanes];
        for (var i = 0; i < lanes; i++)
        {
            _shards[i] = Channel.CreateUnbounded<TurnJob>(new UnboundedChannelOptions
            {
                SingleReader = true,   // one lane loop per shard
                SingleWriter = false,  // any request thread enqueues
            });
        }
    }

    /// <summary>The number of lanes (= <see cref="TurnOptions.MaxConcurrentTurns"/>, floored at 1).</summary>
    public int ShardCount => _shards.Length;

    /// <summary>The reader lane <paramref name="shard"/>'s worker loop drains.</summary>
    public ChannelReader<TurnJob> ReaderFor(int shard) => _shards[shard].Reader;

    /// <summary>
    /// Deterministic-within-process lane selection (string hashes are
    /// per-process randomized - fine: the queue is in-process by design).
    /// Public + pure so tests can pick keys that provably share / split lanes.
    /// </summary>
    public static int ShardFor(TurnKey key, int shardCount) =>
        (int)((uint)key.GetHashCode() % (uint)shardCount);

    /// <inheritdoc />
    public async Task EnqueueAsync(TurnJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await _shards[ShardFor(TurnKey.From(job), _shards.Length)]
            .Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
    }
}
