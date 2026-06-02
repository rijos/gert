using Gert.Service;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Gert.Authentication;

/// <summary>
/// DI wiring for Gert's JWT bearer authentication (auth.md § ASP.NET Core wiring,
/// security F11 alg-pin). Revocation is stateless: it relies on Pocket ID's ~1h
/// access-token lifetime + IdP deactivation, so GERT carries no shared auth state
/// and scales horizontally (decisions §4).
/// </summary>
public static class GertJwtAuthExtensions
{
    /// <summary>The single algorithm we accept — pinned to foreclose alg-confusion / <c>none</c> (F11).</summary>
    public static readonly string[] PinnedAlgorithms = ["RS256"];

    /// <summary>The admin role / group (auth.md authorization matrix).</summary>
    public const string AdminRole = "gert-admins";

    /// <summary>
    /// Register JWT bearer auth pinned to Pocket ID: Authority/Audience from config,
    /// full validation with <c>ValidAlgorithms = ["RS256"]</c> (F11), the Gert claim
    /// mappings, and a 30s clock skew. Also registers <c>IHttpContextAccessor</c> and
    /// <see cref="HttpUserContext"/> as the scoped <see cref="IUserContext"/>. Revocation
    /// is stateless (token expiry + IdP deactivation) — no denylist, no shared state.
    /// </summary>
    public static IServiceCollection AddGertJwtAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, HttpUserContext>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => ConfigureJwtBearer(options, configuration));

        return services;
    }

    /// <summary>
    /// Apply Gert's <see cref="JwtBearerOptions"/> (Authority/Audience, the pinned
    /// <see cref="TokenValidationParameters"/>, and the denylist event). Exposed so tests
    /// can build the exact options the host uses without booting a server.
    /// </summary>
    public static void ConfigureJwtBearer(JwtBearerOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        options.Authority = configuration["Auth:Authority"];
        options.Audience = configuration["Auth:Audience"];

        // Keep claim types verbatim (sub, iss, groups, gert_tools). Without this, the JWT handler
        // remaps short OIDC claims to long WS-* URIs and HttpUserContext can't find "sub".
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = PinnedAlgorithms,   // F11: no alg-confusion / "none"
            NameClaimType = "preferred_username",
            RoleClaimType = "groups",
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    }
}
