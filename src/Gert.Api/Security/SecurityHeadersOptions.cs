namespace Gert.Api.Security;

/// <summary>
/// Inputs to the <see cref="SecurityHeadersMiddleware"/> — chiefly the Pocket ID
/// origin that <c>connect-src</c> must allow (security F1, the exfiltration brake).
/// The origin is derived from <c>Auth:Authority</c> at startup so the CSP lists
/// exactly <c>'self'</c> plus the IdP and nothing else.
/// </summary>
public sealed class SecurityHeadersOptions
{
    /// <summary>
    /// The Pocket ID origin (<c>scheme://host[:port]</c>) added to <c>connect-src</c>
    /// (the SPA's token exchange / refresh target). Empty when no authority is
    /// configured (e.g. the Testing host), in which case <c>connect-src</c> is just
    /// <c>'self'</c>.
    /// </summary>
    public string PocketIdOrigin { get; set; } = string.Empty;

    /// <summary>
    /// The separate origin that serves rendered HTML artifacts (F3), added to
    /// <c>frame-src</c> so the SPA may embed it. Empty → artifacts are framed
    /// same-origin and <c>frame-src</c> stays <c>'self'</c>.
    /// </summary>
    public string ArtifactOrigin { get; set; } = string.Empty;

    /// <summary>
    /// Derive the scheme+host+port origin of a configured authority URL, or an empty
    /// string when it is missing/unparseable. Strips any path/query so only the
    /// origin lands in <c>connect-src</c>.
    /// </summary>
    public static string OriginOf(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority) ||
            !Uri.TryCreate(authority, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
