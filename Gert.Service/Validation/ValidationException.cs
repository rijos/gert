namespace Gert.Service.Validation;

/// <summary>
/// Thrown by the service layer when an input DTO fails validation
/// (principles.md #6 — input is the security boundary). It carries the
/// <see cref="ValidationResult"/> so the host can map it to a 400 ProblemDetails
/// that lists the field errors. The throw happens <b>before</b> any disk/model
/// touch (fail-closed), so a thrown exception means nothing was persisted.
/// </summary>
public sealed class ValidationException : Exception
{
    public ValidationException(ValidationResult result)
        : base(BuildMessage(result))
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>The failing validation result — its <see cref="ValidationResult.Errors"/> drive the 400 body.</summary>
    public ValidationResult Result { get; }

    private static string BuildMessage(ValidationResult result)
    {
        if (result is null || result.Errors.Count == 0)
        {
            return "Invalid request.";
        }

        return string.Join("; ", result.Errors.Select(e => $"{e.Property}: {e.Message}"));
    }
}
