using Gert.Validation;

namespace Gert.Validation.Rules;

/// <summary>
/// Reusable validators for request-supplied <b>route parameters</b> (not DTO bodies):
/// the admin folder <c>{key}</c> (security F6) and the project <c>{pid}</c>
/// (configuration.md section 2.5). These feed a filesystem path, so they are validated
/// <b>before</b> any path-join / destructive delete. Returns the same
/// <see cref="ValidationResult"/> as the body validators, so a failure renders an
/// identical 400. The shape predicates live in <see cref="ValidationRules"/> so the
/// logic stays unit-testable and reusable inside FluentValidation rules elsewhere.
/// </summary>
public static class RouteParamValidation
{
    /// <summary>
    /// Validate an admin user <c>{key}</c> against <c>^[0-9a-f]{64}$</c> (F6). A
    /// failed result must stop the request before <c>{key}</c> is path-joined.
    /// </summary>
    public static ValidationResult ValidateAdminKey(string? key)
    {
        if (ValidationRules.IsWellFormedAdminKey(key))
        {
            return ValidationResult.Success;
        }

        return ValidationResult.Failure(
        [
            new ValidationError
            {
                Property = "key",
                Message = "Admin user key must match ^[0-9a-f]{64}$.",
                Code = "admin_key.invalid",
            },
        ]);
    }

    /// <summary>
    /// Validate a project <c>{pid}</c>: a UUID or the literal <c>default</c>
    /// (configuration.md section 2.5). Defence-in-depth - isolation is structural, but a
    /// malformed pid must never reach a path-join.
    /// </summary>
    public static ValidationResult ValidateProjectId(string? pid)
    {
        if (ValidationRules.IsWellFormedProjectId(pid))
        {
            return ValidationResult.Success;
        }

        return ValidationResult.Failure(
        [
            new ValidationError
            {
                Property = "pid",
                Message = "Project id must be a UUID or the literal 'default'.",
                Code = "pid.invalid",
            },
        ]);
    }
}
