using FluentValidation;
using Gert.Tools;

namespace Gert.Validation.Validators;

/// <summary>
/// Validates the save-memory tool's args (<c>save_memory</c>): a non-empty
/// <c>title</c> and <c>content</c>. Presence-only by design - the tool maps these onto
/// a <c>CreateMemoryRequest</c> and re-proves THAT (the authoritative length /
/// safe-text caps), so imposing those caps here too would double-bind them.
/// </summary>
public sealed class SaveMemoryArgsValidator : AbstractValidator<SaveMemoryArgs>
{
    public SaveMemoryArgsValidator()
    {
        RuleFor(a => a.Title)
            .Must(v => !string.IsNullOrWhiteSpace(v))
                .WithMessage("the 'title' argument is required")
                .WithErrorCode("title.empty");

        RuleFor(a => a.Content)
            .Must(v => !string.IsNullOrWhiteSpace(v))
                .WithMessage("the 'content' argument is required")
                .WithErrorCode("content.empty");
    }
}
