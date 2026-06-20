namespace Gert.Tools.Args;

/// <summary>
/// Arguments for the todo tool (<c>set_todos</c>): the WHOLE list, replace-not-patch.
/// Each entry is a <see cref="TodoArg"/> (text + status) - a leaf arg type, not the
/// model <c>TodoItem</c>, because the wire status is a raw string the validator
/// checks for membership before the tool maps it onto <c>TodoStatus</c>.
/// </summary>
public sealed record TodoArgs
{
    /// <summary>The complete todo list, in order (required, capped at 50).</summary>
    public IReadOnlyList<TodoArg> Todos { get; init; } = [];
}
