using FluentValidation;
using Gert.Model.Dtos;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates a partial <see cref="UpdateProjectRequest"/> (rest-api.md
/// section projects): every field optional (PATCH); a supplied name/description/
/// instructions must be safe text and defaults clear their own validator.
/// </summary>
public sealed class UpdateProjectRequestValidator : AbstractValidator<UpdateProjectRequest>
{
    public UpdateProjectRequestValidator(ProjectDefaultsValidator defaultsValidator)
    {
        ArgumentNullException.ThrowIfNull(defaultsValidator);

        RuleFor(r => r.Name).OptionalSafeText(ValidationRules.ShortTextMax);
        RuleFor(r => r.Description).OptionalSafeText(ValidationRules.MediumTextMax);
        RuleFor(r => r.Instructions).OptionalSafeText(ValidationRules.MediumTextMax);
        RuleFor(r => r.Defaults!).SetValidator(defaultsValidator).When(r => r.Defaults is not null);
    }
}
