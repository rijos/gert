using FluentValidation;
using Gert.Tools.Args;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates the canvas list tool's args (<c>list_artifacts</c>): there are none, so
/// there are no rules - the validator exists only to satisfy the fail-closed base
/// (every <c>ToolCall&lt;TArgs, _&gt;</c> needs a registered <c>IValidator&lt;TArgs&gt;</c>).
/// </summary>
public sealed class ListArtifactsArgsValidator : AbstractValidator<ListArtifactsArgs>
{
    public ListArtifactsArgsValidator()
    {
    }
}
