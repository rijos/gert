using System.Runtime.CompilerServices;
using Gert.Model.Events;
using Gert.Service.Chat.Bus;

namespace Gert.Service.Chat;

/// <summary>
/// <see cref="IConversationStreamer"/> over the bus + the durable log. The
/// gap/dup-free splice depends on strict ordering:
/// <list type="number">
///   <item><b>Subscribe first.</b> Anything the runner publishes from now on is
///   buffered in the subscription.</item>
///   <item><b>Replay from the DB.</b> Everything persisted before (and during)
///   the subscribe is read here - the runner persists BEFORE it publishes, so
///   an event is always in at least one of {replay, buffer}.</item>
///   <item><b>Drain live, dedup by watermark.</b> Events seen in both land in
///   the buffer too; <c>seq &lt;= watermark</c> drops them.</item>
/// </list>
/// If the bus drops the subscription (slow-consumer overflow), the outer loop
/// re-splices from the watermark - correctness never depends on the bus.
/// </summary>
public sealed class ConversationStreamer : IConversationStreamer
{
    /// <summary>Catch-up page size; large enough that replay is 1-2 reads.</summary>
    private const int ReplayPageSize = 500;

    private readonly IConversationBus _bus;
    private readonly IConversationReader _reader;
    private readonly IUserContext _user;

    public ConversationStreamer(IConversationBus bus, IConversationReader reader, IUserContext user)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TurnEvent> StreamAsync(
        string pid,
        string conversationId,
        long afterSeq,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pid);
        ArgumentNullException.ThrowIfNull(conversationId);

        var topic = new ConversationTopic(_user.Iss, _user.Sub, pid, conversationId);
        var watermark = afterSeq;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var subscription = _bus.Subscribe(topic);

            // Replay everything past the watermark from the durable log.
            while (true)
            {
                var page = await _reader
                    .ReadRangeAsync(pid, conversationId, watermark, ReplayPageSize, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var turnEvent in page.Events)
                {
                    watermark = turnEvent.Seq;
                    yield return turnEvent;
                }

                if (!page.HasMore)
                {
                    break;
                }
            }

            // Live tail; the watermark drops the replay/live overlap. Completion
            // without cancellation means the bus dropped us - re-splice.
            await foreach (var turnEvent in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (turnEvent.Seq <= watermark)
                {
                    continue;
                }

                watermark = turnEvent.Seq;
                yield return turnEvent;
            }
        }
    }
}
