namespace Gert.Service.Validation;

/// <summary>The outcome of a validation pass — valid, or a list of errors.</summary>
public sealed record ValidationResult
{
    public required bool IsValid { get; init; }

    public IReadOnlyList<ValidationError> Errors { get; init; } = [];

    /// <summary>A successful result with no errors.</summary>
    public static ValidationResult Success { get; } = new() { IsValid = true };

    /// <summary>Build a failed result from one or more errors.</summary>
    public static ValidationResult Failure(IReadOnlyList<ValidationError> errors) =>
        new() { IsValid = false, Errors = errors };
}
