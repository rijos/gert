using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Authentication;

/// <summary>
/// Authorization policy wiring (auth.md authorization matrix): the <c>Admin</c> policy
/// gating the two <c>/api/admin/*</c> endpoints, and a fallback policy that requires an
/// authenticated user on every other endpoint.
/// </summary>
public static class GertAuthorizationExtensions
{
    /// <summary>Policy name for the admin surface (<c>RequireRole("gert-admins")</c>).</summary>
    public const string AdminPolicy = "Admin";

    /// <summary>
    /// Register the <see cref="AdminPolicy"/> and an authenticated-user fallback policy
    /// (so any endpoint without an explicit policy still needs a valid token; only
    /// <c>[AllowAnonymous]</c> endpoints such as <c>GET /healthz</c> are open).
    /// </summary>
    public static IServiceCollection AddGertAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                AdminPolicy,
                policy => policy.RequireRole(GertJwtAuthExtensions.AdminRole));

            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }
}
