using FluentValidation;
using Gert.Tools.Args;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators.ToolArgs;

/// <summary>
/// Validates the canvas read tool's args (<c>read_artifact</c>): a safe, non-empty
/// <c>name</c>. A supplied <c>range</c> must be exactly two line numbers [start, end];
/// a wrong-length range is ignored by the tool (reads the whole file), as before, and
/// a non-integer entry never reaches validation - the typed deserialize rejects it.
/// </summary>
public sealed class ReadArtifactArgsValidator : AbstractValidator<ReadArtifactArgs>
{
    public ReadArtifactArgsValidator()
    {
        RuleFor(a => a.Name).SafeText(ValidationRules.ShortTextMax);
    }
}
