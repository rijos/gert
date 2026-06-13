namespace Gert.Service.Validation;

/// <summary>
/// Thrown by <see cref="FluentValidationProvider"/> when it is asked to validate a
/// type that has <b>no</b> registered <c>IValidator&lt;T&gt;</c>. This is the
/// fail-closed posture made concrete (principle #6, security): an unvalidated input
/// type is a <i>misconfiguration</i>, never a silent pass. It is distinct from
/// <see cref="ValidationException"/> (a request that failed its rules) - this means
/// the wiring itself is wrong, so it surfaces as a server fault, and the
/// reflection meta-test keeps it from ever happening in production.
/// </summary>
public sealed class ValidatorNotRegisteredException : InvalidOperationException
{
    public ValidatorNotRegisteredException(Type dtoType)
        : base(
            $"No IValidator<{(dtoType ?? throw new ArgumentNullException(nameof(dtoType))).FullName}> is registered. " +
            "Validation is fail-closed: every request DTO a service accepts must have a validator " +
            "(principle #6). Register one in AddGertServices.")
    {
        DtoType = dtoType;
    }

    /// <summary>The DTO type that lacked a registered validator.</summary>
    public Type DtoType { get; }
}
