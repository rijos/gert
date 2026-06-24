namespace Gert.Tools.Results;

/// <summary>
/// The model-facing echo of the todo tool: the accepted list (snake_case statuses)
/// plus the within-turn "keep going" <see cref="Reminder"/> - distinct from the
/// cross-turn revival reminder. The card's <c>Todos</c> side-channel carries the
/// typed list separately.
/// </summary>
public sealed record TodoToolResult
{
    public required IReadOnlyList<TodoEcho> Todos { get; init; }

    public required string Reminder { get; init; }
}
