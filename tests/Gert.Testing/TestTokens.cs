using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Gert.Testing;

/// <summary>
/// Self-contained RS256 JWT minting for the .NET test tiers (testing.md §4.3).
/// Generates an <b>ephemeral</b> RSA key per instance, exposes the matching JWKS
/// (so the host can be configured to validate against it), and mints tokens that
/// travel the real RS256/JWKS path. Nothing is shared with Python and nothing is
/// committed — the only difference from prod is the key source.
///
/// The three standing roles mirror <c>tools/smoke/tokens.py</c>:
/// <list type="bullet">
///   <item><c>admin</c>   — groups <c>gert-admins</c>, <c>gert_tools</c> <c>*</c>.</item>
///   <item><c>user</c>    — groups <c>gert-users</c>, <c>gert_tools</c> <c>rag search</c>.</item>
///   <item><c>limited</c> — groups <c>gert-users</c>, <c>gert_tools</c> <c>rag</c>.</item>
/// </list>
/// Any other shape is an ad-hoc <see cref="Mint(string, string?, IReadOnlyList{string}?, string?, TimeSpan?, DateTimeOffset?)"/>.
/// </summary>
public sealed class TestTokens : IDisposable
{
    /// <summary>Default issuer (the dev authority) — the folder key is sha256(iss + sub).</summary>
    public const string DefaultIssuer = "https://id.test.local";

    /// <summary>Default audience — this API's client id (auth.md).</summary>
    public const string DefaultAudience = "gert-api";

    private readonly RSA _rsa;
    private readonly RsaSecurityKey _key;
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Create with a fresh 2048-bit ephemeral RSA key.</summary>
    public TestTokens(string issuer = DefaultIssuer, string audience = DefaultAudience)
    {
        Issuer = issuer;
        Audience = audience;

        _rsa = RSA.Create(2048);
        _key = new RsaSecurityKey(_rsa) { KeyId = Guid.NewGuid().ToString("N") };
    }

    /// <summary>The issuer stamped on minted tokens and advertised for validation.</summary>
    public string Issuer { get; }

    /// <summary>The audience stamped on minted tokens.</summary>
    public string Audience { get; }

    /// <summary>The signing key — wire this into the host's <c>TokenValidationParameters</c>.</summary>
    public SecurityKey SigningKey => _key;

    /// <summary>This key id (<c>kid</c>), matching the JWKS entry.</summary>
    public string KeyId => _key.KeyId!;

    /// <summary>The public JWKS as a JSON string — point the host's JwtBearer at this.</summary>
    public string JwksJson
    {
        get
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(
                new RsaSecurityKey(_rsa.ExportParameters(includePrivateParameters: false))
                {
                    KeyId = _key.KeyId,
                });
            jwk.Use = "sig";
            jwk.Alg = SecurityAlgorithms.RsaSha256;

            return JsonSerializer.Serialize(new { keys = new[] { jwk } });
        }
    }

    /// <summary>Mint a token for the <c>admin</c> role.</summary>
    public string MintAdmin(params (string Type, string Value)[] extraClaims) =>
        Mint("dev-admin", Issuer, ["gert-admins"], "*", extraClaims: extraClaims);

    /// <summary>Mint a token for the standard <c>user</c> role (sandbox denied).</summary>
    public string MintUser(params (string Type, string Value)[] extraClaims) =>
        Mint("dev-user", Issuer, ["gert-users"], "rag search", extraClaims: extraClaims);

    /// <summary>Mint a token for the <c>limited</c> role (only <c>rag</c>; search + sandbox denied).</summary>
    public string MintLimited(params (string Type, string Value)[] extraClaims) =>
        Mint("dev-limited", Issuer, ["gert-users"], "rag", extraClaims: extraClaims);

    /// <summary>Mint a token by named role: <c>admin</c> | <c>user</c> | <c>limited</c>.</summary>
    public string MintRole(string role) => role switch
    {
        "admin" => MintAdmin(),
        "user" => MintUser(),
        "limited" => MintLimited(),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role; use admin|user|limited."),
    };

    /// <summary>
    /// Mint an RS256 JWT with ad-hoc claims, stamping <c>iss</c>/<c>aud</c>/<c>exp</c>/<c>iat</c>/<c>nbf</c>.
    /// <paramref name="groups"/> become repeated <c>groups</c> claims; <paramref name="gertTools"/>
    /// becomes the <c>gert_tools</c> claim (omitted when null, to exercise the default-grant path).
    /// </summary>
    public string Mint(
        string sub,
        string? iss = null,
        IReadOnlyList<string>? groups = null,
        string? gertTools = "rag search",
        TimeSpan? lifetime = null,
        DateTimeOffset? now = null,
        params (string Type, string Value)[] extraClaims)
    {
        ArgumentNullException.ThrowIfNull(sub);

        var issuer = iss ?? Issuer;
        var issuedAt = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var notBefore = issuedAt;
        var expires = issuedAt + (lifetime ?? TimeSpan.FromHours(1));

        var claims = new List<Claim>
        {
            new("sub", sub),
            new("preferred_username", sub),
        };

        foreach (var g in groups ?? [])
        {
            claims.Add(new Claim("groups", g));
        }

        if (gertTools is not null)
        {
            claims.Add(new Claim("gert_tools", gertTools));
        }

        foreach (var (type, value) in extraClaims ?? [])
        {
            claims.Add(new Claim(type, value));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = Audience,
            IssuedAt = issuedAt,
            NotBefore = notBefore,
            Expires = expires,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        };

        return _handler.CreateToken(descriptor);
    }

    /// <inheritdoc />
    public void Dispose() => _rsa.Dispose();
}
