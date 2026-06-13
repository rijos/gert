using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Gert.Api.Security;

/// <summary>
/// Mints and validates the short-lived, HMAC-signed <b>capability tickets</b> that
/// let the SPA frame an auth-protected HTML artifact from the separate artifact
/// origin (security F3, served-document hardening).
/// <para>
/// The problem: the rendered artifact runs on a <i>separate origin</i> (a sandbox
/// subdomain in prod, a second port in dev) so it gets its own non-inherited CSP
/// and true origin isolation - but a cross-origin <c>&lt;iframe src&gt;</c> is a
/// browser navigation that cannot carry the in-memory bearer (F2). So the app
/// (which <i>does</i> hold the bearer) mints a ticket <b>only after</b> the normal
/// authed, <c>pid</c>-scoped artifact lookup succeeds, and the raw endpoint trusts
/// the ticket instead of a bearer.
/// </para>
/// The ticket binds <c>(iss, sub, pid, artifactId, exp)</c>: the user identity so
/// the raw endpoint can resolve the requester's per-user storage (the artifact
/// store is keyed on iss+sub), the pid+id so it can only ever name that user's own
/// artifact (IDOR-safe), and a short expiry (replay cap).
/// <para>
/// Format: <c>base64url(json-payload) "." base64url(HMAC-SHA256 of the payload)</c>.
/// JSON (not a delimited string) because <c>iss</c> is a URL containing ':'.
/// </para>
/// </summary>
public sealed class ArtifactTicketService
{
    private const char SigSeparator = '.';

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    /// <summary>How long a minted ticket stays valid - long enough to load the
    /// iframe, short enough to make a leaked URL near-useless.</summary>
    public TimeSpan Lifetime { get; }

    /// <summary>The artifact origin URLs are built against - the configured separate
    /// origin, or empty for an origin-relative (same-origin) URL.</summary>
    public string Origin { get; }

    public ArtifactTicketService(
        IOptions<ArtifactTicketOptions> options,
        TimeProvider? clock = null)
    {
        // Accessing .Value also runs the registered IValidateOptions (the weak-
        // secret guard) for hosts that resolve this before ValidateOnStart fires.
        var value = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _key = value.ResolveKeyBytes();
        Lifetime = value.Lifetime;
        Origin = value.Origin ?? string.Empty;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The signed claims bound into a ticket.</summary>
    public sealed record Payload(
        [property: JsonPropertyName("iss")] string Iss,
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("pid")] string Pid,
        [property: JsonPropertyName("id")] string ArtifactId,
        [property: JsonPropertyName("exp")] long Exp);

    /// <summary>Mint a ticket for an artifact the caller has already been authorized
    /// to read. The identity + (pid, id) are baked into the signature, so the raw
    /// endpoint resolves the requester's storage and artifact with no other input.</summary>
    public string Mint(string iss, string sub, string pid, string artifactId)
    {
        ArgumentException.ThrowIfNullOrEmpty(iss);
        ArgumentException.ThrowIfNullOrEmpty(sub);
        ArgumentException.ThrowIfNullOrEmpty(pid);
        ArgumentException.ThrowIfNullOrEmpty(artifactId);

        var exp = _clock.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        var payload = new Payload(iss, sub, pid, artifactId, exp);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJson);
        var encoded = Base64Url(payloadBytes);
        var sig = Base64Url(Sign(encoded));
        return $"{encoded}{SigSeparator}{sig}";
    }

    /// <summary>
    /// Validate a ticket: signature (constant-time), expiry, and shape. Returns true
    /// and the bound <paramref name="payload"/> on success.
    /// </summary>
    public bool TryValidate(string? ticket, out Payload payload)
    {
        payload = default!;
        if (string.IsNullOrEmpty(ticket))
        {
            return false;
        }

        var dot = ticket.IndexOf(SigSeparator);
        if (dot <= 0 || dot == ticket.Length - 1)
        {
            return false;
        }

        var encoded = ticket[..dot];
        byte[] presentedSig;
        byte[] payloadBytes;
        try
        {
            presentedSig = FromBase64Url(ticket[(dot + 1)..]);
            payloadBytes = FromBase64Url(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        // Constant-time signature check (over the encoded payload) before trusting
        // any field.
        if (!CryptographicOperations.FixedTimeEquals(presentedSig, Sign(encoded)))
        {
            return false;
        }

        Payload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Payload>(payloadBytes, PayloadJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null ||
            string.IsNullOrEmpty(parsed.Iss) || string.IsNullOrEmpty(parsed.Sub) ||
            string.IsNullOrEmpty(parsed.Pid) || string.IsNullOrEmpty(parsed.ArtifactId))
        {
            return false;
        }

        if (_clock.GetUtcNow().ToUnixTimeSeconds() > parsed.Exp)
        {
            return false; // expired
        }

        payload = parsed;
        return true;
    }

    private byte[] Sign(string encodedPayload)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(Encoding.ASCII.GetBytes(encodedPayload));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + ((4 - (b.Length % 4)) % 4), '='));
    }
}
