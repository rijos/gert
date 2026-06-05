using System.Net;
using System.Net.Sockets;

namespace Gert.External.Search;

/// <summary>
/// Pure, network-free SSRF policy (security F5). It answers two questions used by the
/// SearXNG fetch path: is a <b>URL</b> allowed (scheme + shape), and is a resolved
/// <b>IP address</b> allowed (not in a blocked range). Keeping the decision logic here
/// — independent of any <c>HttpClient</c> — is what makes the control unit-testable:
/// feed it URLs and IPs, assert allow/deny, no network needed.
///
/// <para>
/// The <see cref="SafeHttpFetcher"/> wires this into the real fetch as the enforcement
/// point: it vets the initial URL, then resolves + checks the destination IP via a
/// <c>SocketsHttpHandler.ConnectCallback</c> (so a DNS-rebind can't slip a private IP
/// past us), and re-runs <see cref="IsUrlAllowed"/> on each redirect target.
/// </para>
///
/// <para><b>Schemes:</b> only <c>http</c> / <c>https</c>; <c>file:</c>, <c>gopher:</c>,
/// <c>ftp:</c>, <c>data:</c>, etc. are denied. <b>IP ranges blocked</b> (IPv4 + IPv6):
/// loopback, private (RFC1918), link-local (incl. the cloud metadata IP
/// <c>169.254.169.254</c>), carrier-grade NAT, IPv4 unspecified/broadcast, IPv6
/// unspecified, unique-local (<c>fc00::/7</c>), IPv6 link-local (<c>fe80::/10</c>), and
/// IPv4-mapped/compatible IPv6 (re-checked against the embedded IPv4).</para>
/// </summary>
public static class SsrfGuard
{
    /// <summary>
    /// True if the URL is well-formed, absolute, and uses <c>http</c>/<c>https</c>.
    /// Does <b>not</b> resolve DNS — host→IP vetting is <see cref="IsIpAllowed"/>,
    /// applied at connect time so DNS rebinding cannot bypass it.
    /// </summary>
    public static bool IsUrlAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // A literal IP host can be vetted up-front; a DNS name is vetted at connect.
        // Strip the brackets a URI keeps around an IPv6 literal host before parsing.
        var host = uri.Host;
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return IsIpAllowed(literal);
        }

        return !string.IsNullOrEmpty(host);
    }

    /// <summary>Convenience overload over a URL string.</summary>
    public static bool IsUrlAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsUrlAllowed(uri);

    /// <summary>
    /// True if the resolved address is a public, routable destination — i.e. <b>not</b>
    /// loopback / private / link-local / unique-local / metadata / unspecified /
    /// broadcast / CGNAT. Applies to both IPv4 and IPv6, and unwraps IPv4-mapped IPv6.
    /// </summary>
    public static bool IsIpAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Unwrap IPv4-mapped / IPv4-compatible IPv6 so we vet the real IPv4.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsIpv4Allowed(address),
            AddressFamily.InterNetworkV6 => IsIpv6Allowed(address),
            _ => false, // anything exotic is denied
        };
    }

    private static bool IsIpv4Allowed(IPAddress address)
    {
        Span<byte> b = stackalloc byte[4];
        if (!address.TryWriteBytes(b, out var written) || written != 4)
        {
            return false;
        }

        // 0.0.0.0/8 — "this" network / unspecified.
        if (b[0] == 0)
        {
            return false;
        }

        // 10.0.0.0/8 — private.
        if (b[0] == 10)
        {
            return false;
        }

        // 127.0.0.0/8 — loopback.
        if (b[0] == 127)
        {
            return false;
        }

        // 169.254.0.0/16 — link-local, incl. the 169.254.169.254 metadata IP.
        if (b[0] == 169 && b[1] == 254)
        {
            return false;
        }

        // 172.16.0.0/12 — private.
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
        {
            return false;
        }

        // 192.168.0.0/16 — private.
        if (b[0] == 192 && b[1] == 168)
        {
            return false;
        }

        // 100.64.0.0/10 — carrier-grade NAT.
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
        {
            return false;
        }

        // 255.255.255.255 — limited broadcast.
        if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255)
        {
            return false;
        }

        return true;
    }

    private static bool IsIpv6Allowed(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return false;
        }

        Span<byte> b = stackalloc byte[16];
        if (!address.TryWriteBytes(b, out var written) || written != 16)
        {
            return false;
        }

        // ::1 loopback and :: unspecified.
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        // fc00::/7 — unique-local addresses (ULA). Top 7 bits == 1111110.
        if ((b[0] & 0xFE) == 0xFC)
        {
            return false;
        }

        // fe80::/10 — link-local (belt-and-braces with IsIPv6LinkLocal).
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)
        {
            return false;
        }

        return true;
    }
}
