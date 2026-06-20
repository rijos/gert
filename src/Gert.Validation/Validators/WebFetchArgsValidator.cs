using FluentValidation;
using Gert.Tools;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates the web-fetch tool's args (<c>web_fetch</c>): a required, absolute
/// http(s) <c>url</c> and a floored <c>max_chars</c>. The url shape is the friendly
/// pre-check (the fetcher re-vets authoritatively - scheme, private ranges, every
/// redirect hop, connect-time DNS pin - security F5). An over-the-ceiling
/// <c>max_chars</c> is clamped by the tool, not errored, so this only floors a
/// supplied value at 1; an omitted value is legitimate (the tool defaults it).
/// </summary>
public sealed class WebFetchArgsValidator : AbstractValidator<WebFetchArgs>
{
    public WebFetchArgsValidator()
    {
        // Cascade Stop so an empty url reports "required" (mentions url), and only a
        // present-but-malformed one reports the http(s) message - matching the tool's
        // old two-step check and the unit tests' expected mentions.
        RuleFor(a => a.Url)
            .Cascade(CascadeMode.Stop)
            .Must(v => !string.IsNullOrWhiteSpace(v))
                .WithMessage("the 'url' argument is required")
                .WithErrorCode("url.empty")
            .Must(IsAbsoluteHttp)
                .WithMessage("url must be an absolute http(s) URL")
                .WithErrorCode("url.invalid");

        RuleFor(a => a.MaxChars!.Value)
            .GreaterThanOrEqualTo(1)
            .When(a => a.MaxChars is not null)
            .WithMessage("max_chars must be at least 1.")
            .WithErrorCode("max_chars.too_small");
    }

    private static bool IsAbsoluteHttp(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
}
