namespace Gert.Validation;

/// <summary>
/// Resolves the validator for a DTO and validates it, returning a consistent
/// result shape (testing.md section validation; principle #6 - input is the security
/// boundary). The real implementation is FluentValidation-backed; this is
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
