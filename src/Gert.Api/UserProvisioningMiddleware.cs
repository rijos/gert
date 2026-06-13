using Gert.Service.Provisioning;

namespace Gert.Api;

/// <summary>
/// Seeds the authenticated caller's user-level state once per request, at the one
/// boundary every request crosses (auth.md section the user context). The databases
/// themselves self-provision on open; this only ensures the descriptive product
/// state - the <c>user.db</c> username row (admin scan) and the landing
/// <c>default</c> project - exists before any controller or WS handler reads it.
/// Runs after authentication, so <see cref="HttpContext.User"/> is populated; skips
/// anonymous requests entirely.
/// </summary>
public sealed class UserProvisioningMiddleware
{
    private readonly RequestDelegate _next;

    public UserProvisioningMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context, IUserProvisioner provisioner)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await provisioner.EnsureCurrentUserAsync(context.RequestAborted).ConfigureAwait(false);
        }

        await _next(context).ConfigureAwait(false);
    }
}
