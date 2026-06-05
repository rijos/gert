using Gert.Model.Chat;
using Gert.Model.Dtos;

namespace Gert.Service.Chat;

/// <summary>
/// The read side of the detached turn pipeline (chat-and-tools.md § detached
/// turns): pages of the durable event log for catch-up/resume/poll, and the
/// thread tree with the orphan rule applied. Always reads <c>chat.db</c> —
/// never the in-process bus — so it is correct across instances and restarts.
/// </summary>
public interface IConversationReader
{
    /// <summary>
    /// Read one page of the conversation's event log: events with
    /// <c>seq &gt; afterSeq</c>, ascending, capped at <paramref name="limit"/>.
    /// </summary>
    Task<ConversationRange> ReadRangeAsync(
        string pid,
        string conversationId,
        long afterSeq,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The conversation thread (messages + tool calls + citations + artifacts)
    /// with effective statuses (orphan rule applied). Null when the conversation
    /// does not exist.
    /// </summary>
    Task<ConversationThread?> GetThreadAsync(
        string pid,
        string conversationId,
        CancellationToken cancellationToken = default);
}
