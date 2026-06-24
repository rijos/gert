using FluentValidation;
using Gert.Tools.Args;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators.ToolArgs;

/// <summary>
/// Validates the web-search tool's args (<c>web_search</c>): a required, safe query.
/// Untrusted model text held to the medium-text bar (length + control/bidi guard).
/// </summary>
public sealed class WebSearchArgsValidator : AbstractValidator<WebSearchArgs>
{
    public WebSearchArgsValidator()
    {
        RuleFor(a => a.Query).SafeText(ValidationRules.MediumTextMax);
    }
}
