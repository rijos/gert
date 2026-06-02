using Microsoft.Extensions.DependencyInjection;

namespace Gert.Service.Validation;

/// <summary>
/// Walking-skeleton <see cref="IValidationProvider"/>: resolves any registered
/// validator for the DTO from the container and runs it; with no validator
/// registered it returns <see cref="ValidationResult.Success"/>.
/// <para>
/// // TODO U6: replace with the fail-closed FluentValidation-backed provider
/// (a DTO with no registered validator is itself a failure) plus the reflection
/// meta-test that asserts every public DTO has a validator (principles.md #6 —
/// input is the security boundary). Do NOT add the FluentValidation package yet.
/// </para>
/// </summary>
public sealed class PassthroughValidationProvider : IValidationProvider
{
    private readonly IServiceProvider _services;

    public PassthroughValidationProvider(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public ValidationResult Validate<T>(T instance)
    {
        // U6 will resolve IValidator<T> here and aggregate its failures. Until the
        // FluentValidation package is added there is no IValidator<T> abstraction
        // to resolve, so nothing is registered and everything passes (passthrough).
        // The IServiceProvider is held so the U6 swap is a body change, not a
        // signature/DI change.
        _ = _services;
        return ValidationResult.Success;
    }
}
