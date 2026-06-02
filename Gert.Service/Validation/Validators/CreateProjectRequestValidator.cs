using FluentValidation;
using Gert.Model.Dtos;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates <see cref="CreateProjectRequest"/> (rest-api.md § projects): a
/// required, safe project name; optional safe description / instructions text;
/// optional defaults that clear their own validator.
/// </summary>
public sealed class CreateProjectRequestValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectRequestValidator(ProjectDefaultsValidator defaultsValidator)
    {
        ArgumentNullException.ThrowIfNull(defaultsValidator);

        RuleFor(r => r.Name).SafeText(ValidationRules.ShortTextMax);
        RuleFor(r => r.Description).OptionalSafeText(ValidationRules.MediumTextMax);
        RuleFor(r => r.Instructions).OptionalSafeText(ValidationRules.MediumTextMax);
        RuleFor(r => r.Defaults!).SetValidator(defaultsValidator).When(r => r.Defaults is not null);
    }
}
