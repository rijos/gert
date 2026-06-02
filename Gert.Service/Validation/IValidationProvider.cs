namespace Gert.Service.Validation;

/// <summary>
/// Resolves the validator for a DTO and validates it, returning a consistent
/// result shape (testing.md § validation; principle #6 — input is the security
/// boundary). The real implementation (U6) is FluentValidation-backed; this is
/// the seam the service/host layers call so invalid input is rejected before it
/// reaches a repository.
/// </summary>
public interface IValidationProvider
{
    /// <summary>
    /// Validate <paramref name="instance"/> against the registered validator for
    /// <typeparamref name="T"/>. A DTO with no registered validator is itself a
    /// failure (fail-closed).
    /// </summary>
    ValidationResult Validate<T>(T instance);
}

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

/// <summary>One validation failure — the offending member and a message.</summary>
public sealed record ValidationError
{
    /// <summary>Dotted member path that failed, e.g. <c>Params.Temperature</c>.</summary>
    public required string Property { get; init; }

    public required string Message { get; init; }

    /// <summary>Optional machine-readable error code.</summary>
    public string? Code { get; init; }
}
