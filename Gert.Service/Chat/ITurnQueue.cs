namespace Gert.Service.Chat;

/// <summary>
/// The seam between POST (enqueue) and the background runner (chat-and-tools.md
/// § detached turns). The host registers the Channel-backed implementation
/// drained by its worker (mirrors <c>IIngestionQueue</c>). In-memory and
/// non-durable by design — the orphan rule (<see cref="MessageStatusRules"/>)
/// covers a lost queue.
/// </summary>
public interface ITurnQueue
{
    /// <summary>Accept a planned turn; returns as soon as it is queued.</summary>
    Task EnqueueAsync(TurnJob job, CancellationToken cancellationToken = default);
}
