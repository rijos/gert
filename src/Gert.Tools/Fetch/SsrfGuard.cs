using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Gert.Tools.Fetch;

/// <summary>
/// Pure, network-free SSRF policy (security F5). It answers two questions used by the
/// SearXNG fetch path: is a <b>URL</b> allowed (scheme + shape), and is a resolved
/// <b>IP address</b> allowed (not in a blocked range). Keeping the decision logic here
/// - independent of any <c>HttpClient</c> - is what makes the control unit-testable:
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
/// <c>ftp:</c>, <c>data:</c>, etc. are denied.</para>
///
/// <para><b>Address family:</b> <b>IPv4 only, for now</b> - any non-IPv4 address (IPv6 in
/// every form, including IPv4-mapped/-compatible and NAT64-embedded) is refused outright.
/// Dropping the IPv6 path also removes the IPv4-mapped / NAT64 unwrap bypasses by
/// construction. <b>IPv4 ranges blocked:</b> unspecified (<c>0.0.0.0/8</c>), private
/// (<c>10/8</c>, <c>172.16/12</c>, <c>192.168/16</c>), loopback (<c>127/8</c>), link-local
/// incl. the cloud metadata IP <c>169.254.169.254</c> (<c>169.254/16</c>), carrier-grade
/// NAT (<c>100.64/10</c>), IETF protocol assignments (<c>192.0.0/24</c>),
/// documentation/test-net (<c>192.0.2/24</c>, <c>198.51.100/24</c>, <c>203.0.113/24</c>),
/// benchmarking (<c>198.18/15</c>), and multicast/reserved/broadcast (<c>224.0.0.0/3</c>,
/// i.e. everything from <c>224</c> up through <c>255.255.255.255</c>).</para>
/// </summary>
public static class SsrfGuard
{
    /// <summary>
    /// True if the URL is well-formed, absolute, and uses <c>http</c>/<c>https</c>.
    /// Does <b>not</b> resolve DNS - host->IP vetting is <see cref="IsIpAllowed"/>,
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
    /// True if the resolved address is a public, routable <b>IPv4</b> destination - i.e.
    /// <b>not</b> loopback / private / link-local / metadata / CGNAT / unspecified /
    /// documentation / benchmarking / multicast / reserved / broadcast. Any non-IPv4
    /// address (IPv6 in every form, including IPv4-mapped/-compatible and NAT64) is denied
    /// outright: the IPv6 path is removed for now, which also closes the mapped/NAT64
    /// unwrap bypasses by construction.
    /// </summary>
    public static bool IsIpAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // IPv4-only policy (for now): anything that is not a plain IPv4 address is refused.
        return address.AddressFamily == AddressFamily.InterNetwork && IsIpv4Allowed(address);
    }

    /// <summary>
    /// True if the destination port is one of the two ports we ever fetch web pages over:
    /// 80 (http) or 443 (https). Scoping to these stops the fetch being turned into a probe
    /// of non-web services (Redis 6379, Postgres 5432, an admin API on :8080, ...) on an
    /// otherwise-public host - the fetcher only ever speaks HTTP(S) anyway.
    /// </summary>
    public static bool IsPortAllowed(int port) => port is 80 or 443;

    /// <summary>
    /// Blocked IPv4 ranges as plain dotted-quad network + prefix length, so the table reads
    /// like the IANA special-purpose-address registry. <see cref="Cidr"/> turns each entry
    /// into a (network, mask) pair once at startup; <see cref="IsIpv4Allowed"/> then just
    /// tests <c>(ip &amp; mask) == network</c>. Listed in ascending network order.
    /// </summary>
    private static readonly (uint Network, uint Mask)[] ReservedIpv4Ranges =
    [
        Cidr("0.0.0.0", 8),       // this-network / unspecified
        Cidr("10.0.0.0", 8),      // private (RFC1918)
        Cidr("100.64.0.0", 10),   // carrier-grade NAT
        Cidr("127.0.0.0", 8),     // loopback
        Cidr("169.254.0.0", 16),  // link-local, incl. the metadata IP 169.254.169.254
        Cidr("172.16.0.0", 12),   // private (RFC1918)
        Cidr("192.0.0.0", 24),    // IETF protocol assignments
        Cidr("192.0.2.0", 24),    // TEST-NET-1 (documentation)
        Cidr("192.168.0.0", 16),  // private (RFC1918)
        Cidr("198.18.0.0", 15),   // benchmarking
        Cidr("198.51.100.0", 24), // TEST-NET-2 (documentation)
        Cidr("203.0.113.0", 24),  // TEST-NET-3 (documentation)
        Cidr("224.0.0.0", 3),     // multicast + reserved + 255.255.255.255 broadcast
    ];

    /// <summary>Parse a dotted-quad network + prefix into its (network, mask) pair (host order).</summary>
    private static (uint Network, uint Mask) Cidr(string network, int prefix)
    {
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var addr = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(network).GetAddressBytes());
        return (addr & mask, mask);
    }

    private static bool IsIpv4Allowed(IPAddress address)
    {
        Span<byte> b = stackalloc byte[4];
        if (!address.TryWriteBytes(b, out var written) || written != 4)
        {
            return false;
        }

        var ip = BinaryPrimitives.ReadUInt32BigEndian(b);
        foreach (var (network, mask) in ReservedIpv4Ranges)
        {
            if ((ip & mask) == network)
            {
                return false;
            }
        }

        return true;
    }
}
