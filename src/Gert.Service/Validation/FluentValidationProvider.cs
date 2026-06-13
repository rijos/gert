using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Service.Validation;

/// <summary>
/// The real, <b>fail-closed</b> <see cref="IValidationProvider"/> (principle #6;
/// replaces a passthrough). It resolves the FluentValidation
/// <c>IValidator&lt;T&gt;</c> for the requested type from the container, runs it,
/// and maps its failures into the host-agnostic <see cref="ValidationResult"/>
/// shape so the API renders a 400 ProblemDetails (testing.md section the provider
/// contract).
/// <para>
/// The default is <b>closed</b>: if no validator is registered for a type it is
/// asked to validate, it <b>throws</b> <see cref="ValidatorNotRegisteredException"/>
/// rather than silently passing - an unvalidated input type is a bug, not an
/// exemption. The reflection meta-test guarantees this throw is never reachable in
/// production.
/// </para>
/// </summary>
public sealed class FluentValidationProvider : IValidationProvider
{
    private readonly IServiceProvider _services;

    public FluentValidationProvider(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public ValidationResult Validate<T>(T instance)
    {
        var validator = _services.GetService<IValidator<T>>()
            ?? throw new ValidatorNotRegisteredException(typeof(T));

        // A null instance is itself invalid input - surface it as a normal failure
        // (the host's 400) rather than a NullReferenceException inside the validator.
        if (instance is null)
        {
            return ValidationResult.Failure(
            [
                new ValidationError
                {
                    Property = string.Empty,
                    Message = "The request body is required.",
                    Code = "request.null",
                },
            ]);
        }

        var result = validator.Validate(instance);
        if (result.IsValid)
        {
            return ValidationResult.Success;
        }

        var errors = new List<ValidationError>(result.Errors.Count);
        foreach (var failure in result.Errors)
        {
            errors.Add(new ValidationError
            {
                Property = failure.PropertyName,
                Message = failure.ErrorMessage,
                Code = failure.ErrorCode,
            });
        }

        return ValidationResult.Failure(errors);
    }
}
