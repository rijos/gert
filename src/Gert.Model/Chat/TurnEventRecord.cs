namespace Gert.Model.Chat;

/// <summary>
/// One persisted streaming event - mirrors a <c>turn_events</c> row in a
/// project's <c>chat.db</c> (storage-and-data.md section chat.db). The table is the
/// durable replay log behind the range/SSE catch-up: the turn runner appends
/// a row per published event; subscribers read <c>seq &gt; cursor</c> to resume
/// without gaps. <see cref="PayloadJson"/> is the <see cref="Events.ChatEvent"/>
/// serialized with the canonical wire contract
/// (<see cref="Json.GertJsonOptions"/>), so transports can round-trip it.
/// </summary>
public sealed record TurnEventRecord
{
    public required string ConversationId { get; init; }

    /// <summary>Per-conversation monotonic sequence (the cursor).</summary>
    public required long Seq { get; init; }

    /// <summary>The ChatEvent wire name (e.g. <c>delta</c>, <c>tool_call</c>).</summary>
    public required string Type { get; init; }

    /// <summary>The serialized <see cref="Events.ChatEvent"/> (wire contract).</summary>
    public required string PayloadJson { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
