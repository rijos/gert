namespace Gert.Service.Validation;

/// <summary>
/// Compile-time evidence that a value of type <typeparamref name="T"/> passed
/// validation (principles.md #6 - input is the security boundary).
/// A service method accepts <c>Validated&lt;T&gt;</c> instead of the raw DTO, so the
/// obligation to validate lives in the type signature: there is no way to call the
/// service without first producing the proof, and no way to produce the proof except
/// through <see cref="From"/>. This replaces the older "validate inside the method
/// body" pattern, which the keystone meta-test could only check existed, never that it
/// ran.
///
/// <para>
/// A <b>sealed class</b>, deliberately, not a <c>readonly struct</c>: a struct's
/// <c>default(Validated&lt;T&gt;)</c> would carry a null <see cref="Value"/> and forge
/// an unvalidated proof that compiles. As a class, <c>default</c> is a null reference,
/// which nullable-reference-types rejects at any non-nullable parameter - and warnings
/// are errors here, so the hole closes at compile time.
/// </para>
///
/// <para>
/// The wrapped DTOs are immutable records, so the proof stays durable: there is no
/// time-of-check/time-of-use gap between <see cref="From"/> and the service reading
/// <see cref="Value"/>.
/// </para>
/// </summary>
/// <typeparam name="T">The validated DTO type.</typeparam>
public sealed class Validated<T>
{
    /// <summary>The validated value. Guaranteed to have passed its validator.</summary>
    public T Value { get; }

    // The private constructor is the entire enforcement mechanism: From is the only
    // path to an instance, and it runs the validator first.
    private Validated(T value) => Value = value;

    /// <summary>
    /// Produce the proof, or throw <see cref="ValidationException"/> on failure. The
    /// throw matches the repository's convention everywhere else, so the existing
    /// <c>ValidationExceptionHandler</c> maps it to a 400 ProblemDetails with field
    /// errors - the boundary behaviour is unchanged, only its location moves.
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
