using System.Security.Cryptography;
using System.Text;

namespace Gert.Api.Security;

/// <summary>
/// Inputs to <see cref="ArtifactTicketService"/> (security F3, served-artifact
/// hardening). Bound from the <c>Artifacts</c> configuration section.
/// </summary>
public sealed class ArtifactTicketOptions
{
    /// <summary>
    /// The separate origin (<c>scheme://host[:port]</c>) that serves rendered HTML
    /// artifacts — a sandbox subdomain in prod, a second port in dev/CI. Empty means
    /// "same origin": the ticket URL is relative and isolation rests on the iframe
    /// sandbox alone (still gets its own non-inherited CSP, just not cross-origin).
    /// </summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>
    /// The minimum UTF-8 byte length an explicit <see cref="Secret"/> must have —
    /// the HMAC-SHA256 block-input floor a brute-forceable short passphrase would
    /// undercut. Enforced fail-fast at startup by
    /// <see cref="SecurityHeadersExtensions.AddGertSecurityHeaders"/> (security F3).
    /// </summary>
    public const int MinimumSecretBytes = 32;

    /// <summary>
    /// HMAC signing key (a secret — env / user-secrets, never <c>appsettings.json</c>;
    /// security F8). When unset a random 32-byte key is generated at startup — fine
    /// for a single instance (tickets are short-lived and process-local); set an
    /// explicit shared secret only when running multiple instances behind a LB. An
    /// explicit secret must be at least <see cref="MinimumSecretBytes"/> UTF-8 bytes.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>Ticket validity window. Long enough to load the iframe, short enough
    /// that a leaked URL is near-useless.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The configured <see cref="Secret"/> as bytes, or a fresh random key.</summary>
    public byte[] ResolveKeyBytes() =>
        string.IsNullOrEmpty(Secret)
            ? RandomNumberGenerator.GetBytes(32)
            : Encoding.UTF8.GetBytes(Secret);
}
