namespace Gert.Model.Events;

/// <summary>
/// <c>message_end</c> - removes the caret; carries the final token count and
/// generation metrics (rest-api.md SSE table).
/// </summary>
public sealed record MessageEndEvent : ChatEvent
{
    /// <summary>Completion token count reported for the turn; null if the model reported none.</summary>
    public int? TokenCount { get; init; }

    /// <summary>Pure generation wall-clock in ms (tool execution excluded) - the tok/s readout.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Context window occupied by the final model round (prompt + completion tokens).</summary>
    public int? ContextTokens { get; init; }

    public override ChatEventType Type => ChatEventType.MessageEnd;
}
