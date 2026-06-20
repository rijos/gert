using FluentValidation;
using Gert.Tools;

namespace Gert.Validation.Validators;

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
