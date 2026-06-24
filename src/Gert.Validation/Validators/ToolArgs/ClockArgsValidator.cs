using FluentValidation;
using Gert.Tools.Args;

namespace Gert.Validation.Validators.ToolArgs;

/// <summary>
/// Validates the clock tool's args (<c>get_datetime</c>): an optional IANA
/// timezone id. Null is legitimate (the tool defaults to the client zone, then UTC),
/// so a supplied id is only shape-checked - a plausible IANA token (the same bar the
/// send-request timezone uses); resolution / unknown-zone stays the tool's graceful
/// error.
/// </summary>
public sealed class ClockArgsValidator : AbstractValidator<ClockArgs>
{
    /// <summary>Max length of an IANA zone id token.</summary>
    public const int TimezoneMax = 64;

    public ClockArgsValidator()
    {
        RuleFor(a => a.Timezone!)
            .Must(t => t.Length <= TimezoneMax && t.All(c =>
                char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '+' or '-' or '.'))
            .When(a => !string.IsNullOrEmpty(a.Timezone))
            .WithMessage("Timezone must be a plausible IANA zone id.")
            .WithErrorCode("timezone.invalid");
    }
}
