using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Gert.Tools.Builtin.Fetch;

/// <summary>
/// Pure, network-free SSRF policy (security F5). Answers two questions for the fetch path:
/// is a <b>URL</b> allowed (scheme + shape), and is a resolved <b>IP</b> allowed (not in a
/// blocked range). Network-free by design so the control is unit-testable.
///
/// <para>
/// <see cref="SafeHttpFetcher"/> is the enforcement point: it vets the initial URL, then
/// checks the destination IP via a <c>SocketsHttpHandler.ConnectCallback</c> (so a
/// DNS-rebind can't slip a private IP past us), and re-runs <see cref="IsUrlAllowed"/> on
/// each redirect target.
/// </para>
///
/// <para><b>Schemes:</b> only <c>http</c>/<c>https</c>. <b>Address family:</b> IPv4 only for
/// now - any non-IPv4 address (IPv6 in every form, incl. IPv4-mapped/-compatible and NAT64)
/// is refused; dropping the IPv6 path also closes the mapped/NAT64 unwrap bypasses by
/// construction. Blocked IPv4 ranges are listed in <see cref="ReservedIpv4Ranges"/>.</para>
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
    /// True if the address is a public, routable IPv4 destination (not in any
    /// <see cref="ReservedIpv4Ranges"/> entry). Any non-IPv4 address is denied outright.
    /// </summary>
    public static bool IsIpAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return address.AddressFamily == AddressFamily.InterNetwork && IsIpv4Allowed(address);
    }

    /// <summary>
    /// True only for ports 80/443. Scoping to these stops the fetch being turned into a probe
    /// of non-web services (Redis 6379, Postgres 5432, an admin API on :8080) on an
    /// otherwise-public host.
    /// </summary>
    public static bool IsPortAllowed(int port) => port is 80 or 443;

    /// <summary>
    /// Blocked IPv4 ranges as dotted-quad network + prefix, reading like the IANA
    /// special-purpose registry. <see cref="Cidr"/> turns each into a (network, mask) pair
    /// at startup; <see cref="IsIpv4Allowed"/> then tests <c>(ip &amp; mask) == network</c>.
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
