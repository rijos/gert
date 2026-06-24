using FluentValidation;
using Gert.Tools.Args;

namespace Gert.Validation.Validators.ToolArgs;

/// <summary>
/// Validates the sub-agent tool's args (<c>run_sub_agent</c>): a non-empty <c>task</c> within
/// <see cref="SubAgentArgs.MaxTaskChars"/> and an optional <c>context</c> within
/// <see cref="SubAgentArgs.MaxContextChars"/> - the DoS brakes that used to be hand-checked in the
/// tool. The args are a brief to a fresh nested conversation, so (unlike user-facing prompt text)
/// they carry no display-safety bar - only the size caps the model must stay under.
/// </summary>
public sealed class SubAgentArgsValidator : AbstractValidator<SubAgentArgs>
{
    public SubAgentArgsValidator()
    {
        RuleFor(a => a.Task)
            .Must(v => !string.IsNullOrWhiteSpace(v))
                .WithMessage("'task' is required")
                .WithErrorCode("sub_agent.task_required")
            .Must(v => v is null || v.Length <= SubAgentArgs.MaxTaskChars)
                .WithMessage($"task is too long (max {SubAgentArgs.MaxTaskChars} characters)")
                .WithErrorCode("sub_agent.task_too_long");

        RuleFor(a => a.Context!)
            .Must(v => v.Length <= SubAgentArgs.MaxContextChars)
                .WithMessage($"context is too long (max {SubAgentArgs.MaxContextChars} characters)")
                .WithErrorCode("sub_agent.context_too_long")
            .When(a => a.Context is not null);
    }
}
