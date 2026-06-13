namespace Gert.Model.Events;

/// <summary>
/// The single mapping from <see cref="ChatEventType"/> to its snake_case SSE wire
/// name (the <c>event:</c> line and the JSON <c>type</c> discriminator) - the one
/// place the strings <c>message_start</c>, <c>tool_call</c>, ... live. The
/// <see cref="JsonDerivedTypeAttribute"/> discriminators on <see cref="ChatEvent"/>
/// must match these exactly so the wire payload round-trips.
/// </summary>
public static class ChatEventTypeNames
{
    private static readonly IReadOnlyDictionary<ChatEventType, string> Names =
        new Dictionary<ChatEventType, string>
        {
            [ChatEventType.MessageStart] = "message_start",
            [ChatEventType.ToolCall] = "tool_call",
            [ChatEventType.ToolResult] = "tool_result",
            [ChatEventType.Reasoning] = "reasoning",
            [ChatEventType.Delta] = "delta",
            [ChatEventType.Citation] = "citation",
            [ChatEventType.Artifact] = "artifact",
            [ChatEventType.QuestionAsked] = "question_asked",
            [ChatEventType.QuestionAnswered] = "question_answered",
            [ChatEventType.MessageEnd] = "message_end",
            [ChatEventType.Cancelled] = "cancelled",
            [ChatEventType.Error] = "error",
        };

    /// <summary>The snake_case SSE/wire name for <paramref name="type"/>.</summary>
    public static string ToWireName(this ChatEventType type) =>
        Names.TryGetValue(type, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(type), type, null);
}
