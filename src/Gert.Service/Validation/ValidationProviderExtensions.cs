namespace Gert.Service.Validation;

/// <summary>
/// Boundary ergonomics for producing a <see cref="Validated{T}"/> proof:
/// <c>_validation.Prove(request)</c> reads better than
/// <c>Validated&lt;TRequest&gt;.From(request, _validation)</c> at the controller call
/// site, and lets the compiler infer <c>T</c>. Resolution stays at the edge (the
/// controller already holds <see cref="IValidationProvider"/>); domain logic never
/// produces proofs.
/// </summary>
public static class ValidationProviderExtensions
{
    /// <summary>Validate <paramref name="value"/> and return the proof (throws on failure).</summary>
    public static Validated<T> Prove<T>(this IValidationProvider validation, T value) =>
        Validated<T>.From(value, validation);
}
