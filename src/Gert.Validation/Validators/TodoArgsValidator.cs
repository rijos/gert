using FluentValidation;
using Gert.Tools;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates the todo tool's args (<c>set_todos</c>): a non-empty <c>todos</c> list
/// capped at <see cref="MaxItems"/> (a runaway model can't flood the card), each entry
/// a valid <see cref="TodoArg"/>. The typed wire collapses a missing list and an
/// explicit empty one to the same empty collection, so both fail here - a meaningful
/// replace always names at least one step.
/// </summary>
public sealed class TodoArgsValidator : AbstractValidator<TodoArgs>
{
    /// <summary>Hard cap on list length - mirrors the tool's old <c>MaxItems</c>.</summary>
    public const int MaxItems = 50;

    public TodoArgsValidator()
    {
        RuleFor(a => a.Todos)
            .Must(t => t is { Count: > 0 })
                .WithMessage("the 'todos' array argument is required")
                .WithErrorCode("todos.empty")
            .Must(t => t.Count <= MaxItems)
                .WithMessage($"too many todos (max {MaxItems})")
                .WithErrorCode("todos.too_many");

        RuleForEach(a => a.Todos).SetValidator(new TodoArgValidator());
    }
}
