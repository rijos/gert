namespace Gert.Model;

/// <summary>
/// Lifecycle of one <see cref="Chat.TodoItem"/> on the model-managed todo list —
/// serialized snake_case on the wire (<c>pending</c> / <c>active</c> / <c>done</c>).
/// </summary>
public enum TodoStatus
{
    Pending,
    Active,
    Done,
}
