using System.Text;

namespace Gert.Api.Security;

/// <summary>
/// DI + pipeline wiring for the <see cref="SecurityHeadersMiddleware"/> (security
/// F1). Binds the Pocket ID origin from <c>Auth:Authority</c> so the CSP's
/// <c>connect-src</c> lists exactly the IdP the SPA talks to.
/// </summary>
public static class SecurityHeadersExtensions
{
    /// <summary>
    /// Register <see cref="SecurityHeadersOptions"/> (IdP origin from
    /// <c>Auth:Authority</c>, artifact origin from <c>Artifacts:Origin</c>) and the
    /// <see cref="ArtifactTicketService"/> used to sign capability URLs (F3).
    /// </summary>
    public static IServiceCollection AddGertSecurityHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var idpOrigin = SecurityHeadersOptions.OriginOf(configuration["Auth:Authority"]);
        var artifactOrigin = SecurityHeadersOptions.OriginOf(configuration["Artifacts:Origin"]);
        services.Configure<SecurityHeadersOptions>(o =>
        {
            o.PocketIdOrigin = idpOrigin;
            o.ArtifactOrigin = artifactOrigin;
        });

        // Ticket signing for the served-artifact origin. Bound once at startup; a
        // missing secret yields a random per-process key (single-instance safe).
        var ticketOptions = new ArtifactTicketOptions { Origin = artifactOrigin };
        configuration.GetSection("Artifacts").Bind(ticketOptions);
        ticketOptions.Origin = artifactOrigin; // normalized origin wins over raw config

        // Fail fast on a weak explicit HMAC key (security F3): a short passphrase
        // makes minted tickets forgeable offline, so refuse to boot rather than run
        // weakened. Unset keeps the random per-process key above.
        if (!string.IsNullOrEmpty(ticketOptions.Secret) &&
            Encoding.UTF8.GetByteCount(ticketOptions.Secret)
                < ArtifactTicketOptions.MinimumSecretBytes)
        {
            throw new InvalidOperationException(
                "Artifacts:Secret is set but too short for an HMAC key. Provide at least " +
                $"{ArtifactTicketOptions.MinimumSecretBytes} bytes (UTF-8) — e.g. " +
                "`openssl rand -base64 32` — or unset it to use a random per-process key.");
        }

        services.AddSingleton(ticketOptions);
        services.AddSingleton<ArtifactTicketService>();

        return services;
    }

    /// <summary>Add the security-headers middleware to the request pipeline.</summary>
    public static IApplicationBuilder UseGertSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
