using System.Text;
using Microsoft.Extensions.Options;

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

        // Computed options (origins derived at startup, not section-bound), so
        // Configure-with-values is the right shape; no annotations to validate
        // (dotnet-style-guide.md §4).
        services.AddOptions<SecurityHeadersOptions>().Configure(o =>
        {
            o.PocketIdOrigin = idpOrigin;
            o.ArtifactOrigin = artifactOrigin;
        });

        // Ticket signing for the served-artifact origin — the §4 options idiom
        // (dotnet-style-guide.md): bind the Artifacts section, fail at startup,
        // not first mint. A missing secret yields a random per-process key
        // (single-instance safe).
        services.AddOptions<ArtifactTicketOptions>()
            .Bind(configuration.GetSection(ArtifactTicketOptions.SectionName))
            .ValidateOnStart();

        // The normalized origin wins over the raw config value (PostConfigure
        // runs after Bind).
        services.PostConfigure<ArtifactTicketOptions>(o => o.Origin = artifactOrigin);

        // Fail fast on a weak explicit HMAC key (security F3): a short passphrase
        // makes minted tickets forgeable offline, so refuse to boot rather than run
        // weakened. ValidateOnStart above runs this at host start.
        services.AddSingleton<IValidateOptions<ArtifactTicketOptions>, ArtifactTicketSecretValidator>();

        services.AddSingleton<ArtifactTicketService>();

        return services;
    }

    /// <summary>Add the security-headers middleware to the request pipeline.</summary>
    public static IApplicationBuilder UseGertSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }

    /// <summary>
    /// Startup guard for <see cref="ArtifactTicketOptions.Secret"/> (security F3):
    /// an explicit HMAC key shorter than
    /// <see cref="ArtifactTicketOptions.MinimumSecretBytes"/> UTF-8 bytes is
    /// rejected — minted tickets would be forgeable offline. Unset keeps the
    /// random per-process key.
    /// </summary>
    private sealed class ArtifactTicketSecretValidator : IValidateOptions<ArtifactTicketOptions>
    {
        public ValidateOptionsResult Validate(string? name, ArtifactTicketOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (!string.IsNullOrEmpty(options.Secret) &&
                Encoding.UTF8.GetByteCount(options.Secret)
                    < ArtifactTicketOptions.MinimumSecretBytes)
            {
                return ValidateOptionsResult.Fail(
                    "Artifacts:Secret is set but too short for an HMAC key. Provide at least " +
                    $"{ArtifactTicketOptions.MinimumSecretBytes} bytes (UTF-8) — e.g. " +
                    "`openssl rand -base64 32` — or unset it to use a random per-process key.");
            }

            return ValidateOptionsResult.Success;
        }
    }
}
