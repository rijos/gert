using FluentValidation;
using Gert.Model.Dtos;

namespace Gert.Service.Validation.Validators;

/// <summary>
/// Validates the <see cref="GenerationParams"/> overrides (configuration.md §4):
/// every field is optional, but a supplied value must sit in a sane range before
/// the server clamps it. Bounds rejected here are obviously-abusive inputs
/// (negative temperature, absurd token counts); the per-deployment clamp is a
/// separate concern higher up.
/// </summary>
public sealed class GenerationParamsValidator : AbstractValidator<GenerationParams>
{
    /// <summary>Upper bound for a requested max-tokens (a DoS brake, not the real clamp).</summary>
    public const int MaxTokensCeiling = 1_000_000;

    /// <summary>Max number of stop sequences accepted.</summary>
    public const int MaxStopSequences = 8;

    /// <summary>Max length of a single stop sequence.</summary>
    public const int MaxStopLength = 64;

    public GenerationParamsValidator()
    {
        RuleFor(p => p.Temperature)
            .Must(t => t is >= 0.0 and <= 2.0)
            .When(p => p.Temperature.HasValue)
            .WithMessage("Temperature must be between 0 and 2.")
            .WithErrorCode("params.temperature");

        RuleFor(p => p.TopP)
            .Must(t => t is >= 0.0 and <= 1.0)
            .When(p => p.TopP.HasValue)
            .WithMessage("top_p must be between 0 and 1.")
            .WithErrorCode("params.top_p");

        RuleFor(p => p.MaxTokens)
            .Must(m => m is >= 1 and <= MaxTokensCeiling)
            .When(p => p.MaxTokens.HasValue)
            .WithMessage($"max_tokens must be between 1 and {MaxTokensCeiling}.")
            .WithErrorCode("params.max_tokens");

        RuleFor(p => p.Stop!)
            .Must(s => s.Count <= MaxStopSequences)
                .WithMessage($"At most {MaxStopSequences} stop sequences are allowed.")
                .WithErrorCode("params.stop_count")
            .Must(s => s.All(seq => !string.IsNullOrEmpty(seq) && seq.Length <= MaxStopLength))
                .WithMessage($"Each stop sequence must be 1–{MaxStopLength} characters.")
                .WithErrorCode("params.stop_length")
            .Must(s => s.All(seq => !ValidationRules.ContainsForbiddenControlChar(seq)))
                .WithMessage("Stop sequences must not contain control characters.")
                .WithErrorCode("params.stop_control")
            .When(p => p.Stop is not null);
    }
}
