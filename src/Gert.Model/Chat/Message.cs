namespace Gert.Model.Chat;

/// <summary>
/// A single message in a conversation - mirrors the <c>messages</c> row in a
/// project's <c>chat.db</c> (storage-and-data.md section chat.db).
/// </summary>
public sealed record Message
{
    /// <summary>UUID primary key.</summary>
    public required string Id { get; init; }

    public required string ConversationId { get; init; }

    public required MessageRole Role { get; init; }

    public required string Content { get; init; }

    /// <summary>
    /// Inline image attachments (pasted into the composer); null/empty for
    /// messages without images - assistant rows never carry any.
    /// </summary>
    public IReadOnlyList<MessageAttachment>? Attachments { get; init; }

    /// <summary>Model that produced this message; null for user/system rows.</summary>
    public string? ModelId { get; init; }

    public int? TokenCount { get; init; }

    /// <summary>The model's thinking text (reasoning_content); null when absent/disabled.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Pure generation wall-clock in ms (tool execution excluded).</summary>
    public long? DurationMs { get; init; }

    /// <summary>Context window occupied by the final model round (prompt + completion tokens).</summary>
    public int? ContextTokens { get; init; }

    /// <summary>
    /// Per-conversation monotonic sequence (the streaming/pagination cursor).
    /// Allocated from <c>conversations.next_seq</c> when the row is written;
    /// 0 on rows that predate the turn pipeline (ordering falls back to
    /// <see cref="CreatedAt"/> for those).
    /// </summary>
    public long Seq { get; init; }

    /// <summary>Lifecycle state; see <see cref="MessageStatus"/> for the orphan rule.</summary>
    public MessageStatus Status { get; init; } = MessageStatus.Complete;

    public required DateTimeOffset CreatedAt { get; init; }
}
