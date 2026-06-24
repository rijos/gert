using FluentValidation;
using Gert.Tools.Args;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators.ToolArgs;

/// <summary>
/// Validates the canvas read tool's args (<c>read_artifact</c>): a safe, non-empty
/// <c>name</c>. The <c>range</c> is not enforced here - a wrong-length range is tolerated
/// by the tool (reads the whole file) and a non-integer entry never reaches validation
/// because the typed deserialize rejects it first.
/// </summary>
public sealed class ReadArtifactArgsValidator : AbstractValidator<ReadArtifactArgs>
{
    public ReadArtifactArgsValidator()
    {
        RuleFor(a => a.Name).SafeText(ValidationRules.ShortTextMax);
    }
}
