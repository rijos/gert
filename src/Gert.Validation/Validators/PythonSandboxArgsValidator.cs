using FluentValidation;
using Gert.Tools.Args;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates the sandbox tool's args (<c>run_python</c>): a required, non-empty
/// <c>code</c> body. Capped at the long-text bar (a DoS brake on the prompt-side
/// payload). Control/bidi chars are NOT forbidden here - Python source legitimately
/// carries tabs and the sandbox is the isolation boundary - so only length + non-empty
/// are enforced.
/// </summary>
public sealed class PythonSandboxArgsValidator : AbstractValidator<PythonSandboxArgs>
{
    public PythonSandboxArgsValidator()
    {
        RuleFor(a => a.Code)
            .Must(v => !string.IsNullOrWhiteSpace(v))
                .WithMessage("the 'code' argument is required")
                .WithErrorCode("code.empty")
            .Must(v => v is null || v.Length <= ValidationRules.LongTextMax)
                .WithMessage($"Must be at most {ValidationRules.LongTextMax} characters.")
                .WithErrorCode("code.too_long");
    }
}
