using FluentValidation;
using Gert.Model.Dtos;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates <see cref="MoveConversationRequest"/> (rest-api.md
/// section conversations): the destination must be a well-formed project id - the
/// same shape gate as the route's <c>{pid}</c> (configuration.md section 2.5), so a
/// malformed target never reaches a path-join.
/// </summary>
public sealed class MoveConversationRequestValidator : AbstractValidator<MoveConversationRequest>
{
    public MoveConversationRequestValidator()
    {
        RuleFor(r => r.TargetPid)
            .Must(ValidationRules.IsWellFormedProjectId)
            .WithMessage("Target project id must be a UUID or the literal 'default'.")
            .WithErrorCode("target_pid.invalid");
    }
}
