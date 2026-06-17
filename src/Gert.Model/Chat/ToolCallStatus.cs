namespace Gert.Model.Chat;

/// <summary>
/// Lifecycle status of a <see cref="ToolCall"/> - mirrors <c>chat.db</c>
/// <c>tool_calls.status</c>.
/// </summary>
public enum ToolCallStatus
{
    Running,
    Done,
    Error,
}
