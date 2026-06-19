namespace Gert.Service.Validation;

/// <summary>
/// Compile-time evidence that a <typeparamref name="T"/> passed validation
/// (principles.md #6 - input is the security boundary). Service methods take
/// <c>Validated&lt;T&gt;</c> instead of the raw DTO, so the obligation to validate lives
/// in the signature: the only way to an instance is <see cref="From"/>, which runs the
/// validator first.
///
/// <para>
/// A <b>sealed class</b>, deliberately, not a <c>readonly struct</c>: a struct's
/// <c>default</c> would carry a null <see cref="Value"/> and forge an unvalidated proof
/// that compiles, whereas a class <c>default</c> is a null reference that
/// nullable-reference-types (warnings-as-errors here) rejects at any non-nullable
/// parameter. The wrapped DTOs are immutable records, so there is no
/// time-of-check/time-of-use gap between <see cref="From"/> and a later read of
/// <see cref="Value"/>.
/// </para>
/// </summary>
/// <typeparam name="T">The validated DTO type.</typeparam>
public sealed class Validated<T>
{
    /// <summary>The validated value. Guaranteed to have passed its validator.</summary>
    public T Value { get; }

    private Validated(T value) => Value = value;

    /// <summary>
    /// Produce the proof, or throw <see cref="ValidationException"/> on failure;
    /// <c>ValidationExceptionHandler</c> maps that to a 400 ProblemDetails with field errors.
    /// </summary>
    public static Validated<T> From(T value, IValidationProvider validation)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(validation);

        var result = validation.Validate(value);
        if (!result.IsValid)
        {
            throw new ValidationException(result);
        }

        return new Validated<T>(value);
    }
}
