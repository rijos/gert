using Gert.Service;
using Serilog.Context;

namespace Gert.Api.Logging;

/// <summary>
/// Pushes request-scoped fields onto the Serilog <see cref="LogContext"/> so every log line
/// for the request carries them (operations.md section Logging format):
/// <list type="bullet">
///   <item><c>comp</c> - <c>api</c> for the HTTP host.</item>
///   <item><c>req</c> - per-request correlation id (<see cref="HttpContext.TraceIdentifier"/>).</item>
///   <item><c>uid</c> - the short identity hash (prefix of <c>sha256(iss+sub)</c>),
///         <b>only when authenticated</b>. The raw <c>sub</c> is never logged.</item>
/// </list>
/// Anonymous requests get no <c>uid</c>, and any failure reading the user context is swallowed
/// so logging never breaks a request.
/// </summary>
public sealed class RequestLogContextMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context, IUserContext userContext)
    {
        ArgumentNullException.ThrowIfNull(context);

        var uid = TryResolveUid(context, userContext);

        using (LogContext.PushProperty("comp", "api"))
        using (LogContext.PushProperty("req", context.TraceIdentifier))
        {
            if (uid is not null)
            {
                using (LogContext.PushProperty("uid", uid))
                {
                    await _next(context).ConfigureAwait(false);
                }
            }
            else
            {
                await _next(context).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The short identity hash for an authenticated caller, or <c>null</c> for anonymous /
    /// any resolution failure. Only the validated token's <c>iss</c>+<c>sub</c> feed the
    /// hash; neither raw value is ever logged.
    /// </summary>
    private static string? TryResolveUid(HttpContext context, IUserContext userContext)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        try
        {
            return UserIdHash.Compute(userContext.Iss, userContext.Sub);
        }
        catch (UnauthorizedAccessException)
        {
            // Authenticated flag set but a claim missing - fail closed (no uid).
            return null;
        }
    }
}
