using Gert.Model.Chat;

namespace Gert.Database;

/// <summary>
/// Per-project <c>chat.db</c> persistence (storage-and-data.md section chat.db). One
/// instance wraps an open connection to a single project's database; dispose it
/// when the unit of work completes (open-per-use). The path is the scope - there
/// is no <c>project_id</c>/<c>user</c> argument, so a query structurally cannot
/// reach another project's rows.
/// </summary>
public interface IChatRepository : IAsyncDisposable
{
    Task<IReadOnlyList<Conversation>> ListConversationsAsync(CancellationToken cancellationToken = default);

    Task<Conversation?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    Task<ConversationThread?> GetThreadAsync(string conversationId, CancellationToken cancellationToken = default);

    Task InsertConversationAsync(Conversation conversation, CancellationToken cancellationToken = default);

    Task UpdateConversationAsync(Conversation conversation, CancellationToken cancellationToken = default);

    Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Message>> ListMessagesAsync(string conversationId, CancellationToken cancellationToken = default);

    Task InsertMessageAsync(Message message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically insert one turn's rows - the user message and the streaming
    /// assistant placeholder - in a single transaction. The placeholder insert is
    /// the per-conversation turn gate (<c>ux_messages_streaming</c>): returns
    /// false when another streaming placeholder already holds the gate (the
    /// caller maps this to the 409 rule), in which case NEITHER row is
    /// persisted. Any other failure throws.
    /// </summary>
    Task<bool> TryInsertTurnMessagesAsync(
        Message userMessage,
        Message assistantMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The planner's orphan write-back: finalize an expired streaming row to
    /// <c>error</c>, conditionally - the UPDATE carries <c>AND status =
    /// 'streaming'</c>, so a row the runner finalized in the meantime is never
    /// clobbered. Content is kept (whatever partial text the dead turn flushed).
    /// Returns true when this call performed the transition.
    /// </summary>
    Task<bool> TryExpireStreamingMessageAsync(
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream-update an assistant row: replace content, set status, and (when
    /// non-null) the token count. Used by the turn runner for incremental flushes
    /// and the final complete/error transition.
    /// </summary>
    Task UpdateMessageStreamAsync(
        string messageId,
        string content,
        MessageStatus status,
        int? tokenCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Final (complete) transition of an assistant row, carrying the turn's
    /// reasoning text and generation metrics alongside the content. Null metric
    /// values keep whatever was written earlier (COALESCE semantics).
    /// </summary>
    Task FinalizeMessageAsync(
        string messageId,
        string content,
        MessageStatus status,
        int? tokenCount,
        string? reasoning,
        long? durationMs,
        int? contextTokens,
        CancellationToken cancellationToken = default);

    // Turn sequencing + the durable event log (chat-and-tools.md section detached turns)

    /// <summary>
    /// Atomically allocate the next per-conversation sequence number
    /// (<c>UPDATE conversations SET next_seq = next_seq + 1 ... RETURNING</c>).
    /// The single source of <c>seq</c> - single-writer per conversation by
    /// construction: the <c>ux_messages_streaming</c> gate index admits at most
    /// one live turn per conversation, and the keyed worker lanes run one
    /// conversation's turns strictly in order on one lane (decisions section 11). A
    /// losing planner's allocations leave gaps in <c>next_seq</c> - harmless:
    /// seq is an ordering cursor, not dense.
    /// </summary>
    Task<long> AllocateSeqAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Append one event to the conversation's durable replay log.</summary>
    Task AppendTurnEventAsync(TurnEventRecord turnEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the replay log: events with <c>seq &gt; afterSeq</c>, ascending, at
    /// most <paramref name="limit"/> rows. The catch-up/resume/poll read.
    /// </summary>
    Task<IReadOnlyList<TurnEventRecord>> ReadTurnEventsAsync(
        string conversationId,
        long afterSeq,
        int limit,
        CancellationToken cancellationToken = default);

    Task InsertToolCallAsync(ToolCall toolCall, CancellationToken cancellationToken = default);

    /// <summary>
    /// The conversation's newest successful (<c>done</c>) tool call of
    /// <paramref name="kind"/>, or null. The read behind the cross-turn todo
    /// reminder: the latest accepted <c>set_todos</c> echo is the list's truth,
    /// so the planner can revive it after history drops the tool messages.
    /// </summary>
    Task<ToolCall?> GetLatestToolCallAsync(
        string conversationId,
        string kind,
        CancellationToken cancellationToken = default);

    Task InsertCitationsAsync(IReadOnlyList<Citation> citations, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Artifact>> ListArtifactsAsync(string conversationId, CancellationToken cancellationToken = default);

    Task<Artifact?> GetArtifactAsync(string artifactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The latest artifact in a conversation carrying <paramref name="name"/>, or
    /// null. Names are the model-facing handle the artifact tools (make/edit/read)
    /// key on; a re-used name "saves over" the prior draft, so this returns the
    /// most recent one.
    /// </summary>
    Task<Artifact?> GetArtifactByNameAsync(
        string conversationId,
        string name,
        CancellationToken cancellationToken = default);

    Task InsertArtifactAsync(Artifact artifact, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrite an existing artifact's mutable fields (kind/name/language/content/
    /// version) by <c>Id</c> - the <c>edit_artifact</c> / <c>make_artifact</c>
    /// (overwrite) path. Identity and conversation binding are immutable.
    /// </summary>
    Task UpdateArtifactAsync(Artifact artifact, CancellationToken cancellationToken = default);
}
