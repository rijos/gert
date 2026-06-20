using Gert.Validation;

namespace Gert.Api.Validation;

/// <summary>
/// Controller-side guards for request-supplied <b>route parameters</b> that feed a
/// filesystem path - the project <c>{pid}</c> (configuration.md section 2.5) and the admin
/// folder <c>{key}</c> (security F6). They wrap <see cref="RouteParamValidation"/> and
/// <b>throw</b> <see cref="ValidationException"/> on a bad shape (same 400 ProblemDetails as
/// body validators), so the request stops <b>before</b> the value reaches a service - never a
/// repo or a destructive delete.
/// </summary>
internal static class RouteParams
{
    /// <summary>
    /// Throw a 400-mapped <see cref="ValidationException"/> unless <paramref name="pid"/>
    /// is a UUID or the literal <c>default</c>. Call at the top of every
    /// <c>{pid}</c>-scoped action, before delegating to a service.
    /// </summary>
    public static void RequireValidProjectId(string? pid)
    {
        var result = RouteParamValidation.ValidateProjectId(pid);
        if (!result.IsValid)
        {
            throw new ValidationException(result);
        }
    }

    /// <summary>
    /// Throw a 400-mapped <see cref="ValidationException"/> unless <paramref name="key"/>
    /// matches <c>^[0-9a-f]{64}$</c> (F6). Call <b>before</b> the admin key is ever
    /// path-joined or handed to the admin service.
    /// </summary>
    public static void RequireValidAdminKey(string? key)
    {
        var result = RouteParamValidation.ValidateAdminKey(key);
        if (!result.IsValid)
        {
            throw new ValidationException(result);
        }
    }
}
