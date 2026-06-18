using Gert.Service;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Gert.Authentication;

/// <summary>
/// DI wiring for Gert's JWT bearer authentication (auth.md section ASP.NET Core wiring,
/// security F11 alg-pin). Revocation is stateless: it relies on Pocket ID's ~1h
/// access-token lifetime + IdP deactivation, so GERT carries no shared auth state
/// and scales horizontally (decisions section 4).
/// </summary>
public static class GertJwtAuthExtensions
{
    /// <summary>The single algorithm we accept - pinned to foreclose alg-confusion / <c>none</c> (F11).</summary>
    public static readonly string[] PinnedAlgorithms = ["RS256"];

    /// <summary>
    /// Accepted token-type (<c>typ</c>) headers. Pocket ID issues OAuth2 <b>access</b> tokens
    /// (auth.md section the SPA flow); RFC 9068 stamps those <c>at+jwt</c>, but many builds
    /// still emit the legacy <c>JWT</c> - both our dev (<c>tools/smoke/tokens.py</c>) and .NET
    /// (<c>TestTokens</c>) minters default to <c>JWT</c>. Pinning the set forecloses replaying a
    /// differently-typed token (e.g. an ID token) as an access token. NOTE: if a Pocket ID build
    /// stamps some other <c>typ</c> (or omits it), validation fails closed - verify against a real
    /// access token before production.
    /// </summary>
    public static readonly string[] AcceptedTokenTypes = ["at+jwt", "JWT"];

    /// <summary>The admin role / group (auth.md authorization matrix).</summary>
    public const string AdminRole = "gert-admins";

    /// <summary>
    /// Register JWT bearer auth pinned to Pocket ID: Authority/Audience from config,
    /// full validation with <c>ValidAlgorithms = ["RS256"]</c> (F11), the Gert claim
    /// mappings, and a 30s clock skew. Also registers <c>IHttpContextAccessor</c> and
    /// <see cref="HttpUserContext"/> as the scoped <see cref="IUserContext"/>. Revocation
    /// is stateless (token expiry + IdP deactivation) - no denylist, no shared state.
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
    /// <see cref="TokenValidationParameters"/>, the verbatim claim mapping). There is
    /// no denylist - revocation is stateless (decisions section 4). Exposed so tests can
    /// build the exact options the host uses without booting a server.
    /// </summary>
    /// <remarks>
    /// Fail-fast: a missing <c>Auth:Authority</c>/<c>Auth:Audience</c> throws rather than
    /// booting an auth scheme that validates nothing. The dev-JWKS and offline-test paths
    /// post-configure these options afterwards (null the Authority, swap the signing key,
    /// drop <see cref="JwtBearerOptions.RequireHttpsMetadata"/>), so the pins below are the
    /// production posture - the test seams relax exactly what they must, nothing more.
    /// </remarks>
    public static void ConfigureJwtBearer(JwtBearerOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        var authority = configuration["Auth:Authority"]
            ?? throw new InvalidOperationException("Auth:Authority is not configured.");
        var audience = configuration["Auth:Audience"]
            ?? throw new InvalidOperationException("Auth:Audience is not configured.");

        options.Authority = authority;
        options.Audience = audience;

        // Fetch JWKS/discovery over TLS only. Default is true; pin it so a stray
        // appsettings override in some environment can't silently disable it. (The
        // dev-JWKS / offline-test seams deliberately set this false - they use no metadata
        // fetch at all and an in-process key.)
        options.RequireHttpsMetadata = true;

        // Keep claim types verbatim (sub, iss, groups, gert_tools). Without this, the JWT handler
        // remaps short OIDC claims to long WS-* URIs and HttpUserContext can't find "sub".
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authority,             // pin explicitly, not just via discovery
            ValidateAudience = true,
            ValidAudience = audience,            // explicit rather than relying on PostConfigure
            ValidateLifetime = true,
            RequireExpirationTime = true,        // reject tokens with no exp
            RequireSignedTokens = true,          // reject unsigned tokens (belt-and-suspenders with the alg pin)
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = PinnedAlgorithms,  // F11: no alg-confusion / "none"
            ValidTypes = AcceptedTokenTypes,     // pin typ so an ID token can't be replayed as an access token
            NameClaimType = "preferred_username",
            RoleClaimType = "groups",
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = ctx =>
            {
                // A failed validation is a security signal - log the cause (never the token).
                ctx.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Gert.Authentication.JwtBearer")
                    .LogWarning(ctx.Exception, "JWT bearer authentication failed.");
                return Task.CompletedTask;
            },
        };
    }
}
