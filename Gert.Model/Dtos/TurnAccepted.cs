namespace Gert.Model.Dtos;

/// <summary>
/// The <c>202 Accepted</c> body of <c>POST …/messages</c> (rest-api.md
/// § sending a message): the ids the planner persisted and the cursor to
/// subscribe from. The client opens WS/SSE with <c>after = seq</c> — every
/// event of the accepted turn has a later seq.
/// </summary>
public sealed record TurnAccepted
{
    public required string ConversationId { get; init; }

    public required string UserMessageId { get; init; }

    /// <summary>The streaming assistant placeholder the turn fills in.</summary>
    public required string AssistantMessageId { get; init; }

    /// <summary>The subscribe cursor (the assistant row's seq).</summary>
    public required long Seq { get; init; }
}
