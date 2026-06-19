namespace Gert.Service.Validation;

/// <summary>The outcome of a validation pass - valid, or a list of errors.</summary>
public sealed record ValidationResult
{
    public required bool IsValid { get; init; }

    public IReadOnlyList<ValidationError> Errors { get; init; } = [];

    public static ValidationResult Success { get; } = new() { IsValid = true };

    public static ValidationResult Failure(IReadOnlyList<ValidationError> errors) =>
        new() { IsValid = false, Errors = errors };
}
