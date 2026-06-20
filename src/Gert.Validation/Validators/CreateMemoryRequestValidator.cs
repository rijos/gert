using FluentValidation;
using Gert.Model.Dtos;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates <see cref="CreateMemoryRequest"/> (rest-api.md section memory): a required,
/// safe title and required, safe body - both embedded into <c>rag.db</c>, so both
/// are untrusted content held to the message-text bar. <c>Pinned</c> is a plain
/// bool - nothing to abuse.
/// </summary>
public sealed class CreateMemoryRequestValidator : AbstractValidator<CreateMemoryRequest>
{
    public CreateMemoryRequestValidator()
    {
        RuleFor(r => r.Title).SafeText(ValidationRules.ShortTextMax);
        RuleFor(r => r.Content).SafeText(ValidationRules.LongTextMax);
    }
}
