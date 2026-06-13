namespace Gert.Model.Chat;

/// <summary>
/// One entry on the model-managed todo list (the <c>set_todos</c> tool). The
/// model always sends the WHOLE list, so an item carries no id - the list on the
/// latest tool call is the truth, rendered verbatim on its tool card.
/// </summary>
public sealed record TodoItem
{
    public required string Text { get; init; }

    public required TodoStatus Status { get; init; }
}
