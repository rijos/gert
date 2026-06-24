using FluentValidation;
using Gert.Tools.Args;
using Gert.Validation.Rules;

namespace Gert.Validation.Validators.ToolArgs;

/// <summary>
/// Validates the RAG tool's args (<c>search_documents</c>): a required, safe query
/// (untrusted model text held to the medium-text bar) and, if supplied, a top-k in
/// [1, 20] - the range the tool's <c>k</c> schema advertises. An omitted k is
/// legitimate (the tool defaults it), so the range rule guards a supplied value only.
/// </summary>
public sealed class RagArgsValidator : AbstractValidator<RagArgs>
{
    /// <summary>Top-k bounds, mirroring the tool's <c>k</c> schema (1-20).</summary>
    public const int MinK = 1;
    public const int MaxK = 20;

    public RagArgsValidator()
    {
        RuleFor(a => a.Query).SafeText(ValidationRules.MediumTextMax);

        RuleFor(a => a.K!.Value)
            .InclusiveBetween(MinK, MaxK)
            .When(a => a.K is not null)
            .WithMessage($"k must be between {MinK} and {MaxK}.")
            .WithErrorCode("rag.k_out_of_range");
    }
}
