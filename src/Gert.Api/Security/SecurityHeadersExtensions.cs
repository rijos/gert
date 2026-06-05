namespace Gert.Api.Security;

/// <summary>
/// DI + pipeline wiring for the <see cref="SecurityHeadersMiddleware"/> (security
/// F1). Binds the Pocket ID origin from <c>Auth:Authority</c> so the CSP's
/// <c>connect-src</c> lists exactly the IdP the SPA talks to.
/// </summary>
public static class SecurityHeadersExtensions
{
    /// <summary>Register <see cref="SecurityHeadersOptions"/>, deriving the IdP origin from <c>Auth:Authority</c>.</summary>
    public static IServiceCollection AddGertSecurityHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var origin = SecurityHeadersOptions.OriginOf(configuration["Auth:Authority"]);
        services.Configure<SecurityHeadersOptions>(o => o.PocketIdOrigin = origin);

        return services;
    }

    /// <summary>Add the security-headers middleware to the request pipeline.</summary>
    public static IApplicationBuilder UseGertSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
