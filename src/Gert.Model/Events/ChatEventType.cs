namespace Gert.Model.Events;

/// <summary>
/// The kind of a <see cref="ChatEvent"/> — the closed set of SSE event types in
/// the streamed chat response (rest-api.md § sending a message). Each subtype of
/// <see cref="ChatEvent"/> reports its value via <see cref="ChatEvent.Type"/>;
/// <see cref="ChatEventTypeNames"/> maps each value to its snake_case wire name,
/// which is also the <c>type</c> discriminator on the JSON payload.
/// </summary>
public enum ChatEventType
{
    MessageStart,
    ToolCall,
    ToolResult,
    Reasoning,
    Delta,
    Citation,
    Artifact,
    MessageEnd,
    Cancelled,
    Error,
}
