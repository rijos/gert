using System.Net;
using FluentAssertions;
using Gert.Tools.Fetch;
using Gert.Tools.Search;
using Xunit;

namespace Gert.Tools.Builtin.Tests;

/// <summary>
/// Unit tests for the SSRF policy (security F5). Pure: feed URLs/IPs, assert allow/deny;
/// no network. Covers public-allow vs private/loopback/link-local/metadata/CGNAT/
/// documentation/reserved and non-http scheme deny, the IPv4-only family rule (all IPv6
/// refused for now), and the redirect-to-private case via the URL/IP split (the fetcher
/// re-runs <see cref="SsrfGuard.IsUrlAllowed"/> on each redirect target).
/// </summary>
public sealed class SsrfGuardTests
{
    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("http://example.com")]
    [InlineData("https://93.184.216.34/")] // a public literal IP
    public void IsUrlAllowed_PublicHttpOrHttps_Allows(string url) =>
        SsrfGuard.IsUrlAllowed(url).Should().BeTrue();

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    [InlineData("ftp://example.com/x")]
    [InlineData("data:text/plain,hi")]
    [InlineData("ldap://example.com/")]
    public void IsUrlAllowed_NonHttpScheme_Denies(string url) =>
        SsrfGuard.IsUrlAllowed(url).Should().BeFalse();

    [Theory]
    [InlineData("http://127.0.0.1/")] // loopback
    [InlineData("http://10.0.0.5/")] // private RFC1918
    [InlineData("http://172.16.5.4/")] // private RFC1918
    [InlineData("http://192.168.1.1/")] // private RFC1918
    [InlineData("http://169.254.169.254/latest/meta-data/")] // cloud metadata IP
    [InlineData("http://100.64.0.1/")] // carrier-grade NAT
    [InlineData("http://0.0.0.0/")] // unspecified
    [InlineData("http://192.0.0.1/")] // IETF protocol assignments
    [InlineData("http://192.0.2.5/")] // TEST-NET-1 (documentation)
    [InlineData("http://198.18.0.1/")] // benchmarking
    [InlineData("http://198.51.100.7/")] // TEST-NET-2 (documentation)
    [InlineData("http://203.0.113.9/")] // TEST-NET-3 (documentation)
    [InlineData("http://224.0.0.1/")] // multicast
    [InlineData("http://240.0.0.1/")] // reserved / future use
    [InlineData("http://255.255.255.255/")] // limited broadcast
    public void IsUrlAllowed_PrivateOrSpecialLiteral_Denies(string url) =>
        SsrfGuard.IsUrlAllowed(url).Should().BeFalse();

    [Theory]
    [InlineData("http://[2606:2800:220:1:248:1893:25c8:1946]/")] // public IPv6 literal
    [InlineData("http://[::1]/")] // IPv6 loopback
    [InlineData("http://[fe80::1]/")] // IPv6 link-local
    [InlineData("http://[fc00::1]/")] // IPv6 unique-local
    [InlineData("http://[fd00::1]/")] // IPv6 ULA
    [InlineData("http://[::ffff:127.0.0.1]/")] // IPv4-mapped loopback
    [InlineData("http://[::ffff:169.254.169.254]/")] // IPv4-mapped metadata
    [InlineData("http://[::ffff:93.184.216.34]/")] // IPv4-mapped public - still refused
    [InlineData("http://[64:ff9b::a9fe:a9fe]/")] // NAT64-embedded metadata - refused as IPv6
    public void IsUrlAllowed_AnyIpv6Literal_Denies(string url) =>
        SsrfGuard.IsUrlAllowed(url).Should().BeFalse();

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public void IsUrlAllowed_Malformed_Denies(string url) =>
        SsrfGuard.IsUrlAllowed(url).Should().BeFalse();

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    public void IsIpAllowed_PublicIpv4_Allows(string ip) =>
        SsrfGuard.IsIpAllowed(IPAddress.Parse(ip)).Should().BeTrue();

    [Theory]
    [InlineData("127.0.0.1")] // loopback
    [InlineData("10.1.2.3")] // private RFC1918
    [InlineData("172.16.0.0")] // private RFC1918 low edge
    [InlineData("172.31.255.255")] // private RFC1918 high edge
    [InlineData("192.168.0.1")] // private RFC1918
    [InlineData("169.254.169.254")] // metadata
    [InlineData("100.64.0.0")] // CGNAT low edge
    [InlineData("100.127.0.1")] // CGNAT
    [InlineData("0.0.0.0")] // unspecified
    [InlineData("192.0.0.1")] // IETF protocol assignments
    [InlineData("192.0.2.1")] // TEST-NET-1
    [InlineData("198.18.0.1")] // benchmarking low edge
    [InlineData("198.19.255.255")] // benchmarking high edge
    [InlineData("198.51.100.1")] // TEST-NET-2
    [InlineData("203.0.113.1")] // TEST-NET-3
    [InlineData("224.0.0.1")] // multicast
    [InlineData("239.255.255.255")] // multicast high edge
    [InlineData("240.0.0.1")] // reserved / future
    [InlineData("255.255.255.255")] // limited broadcast
    public void IsIpAllowed_PrivateOrSpecialIpv4_Denies(string ip) =>
        SsrfGuard.IsIpAllowed(IPAddress.Parse(ip)).Should().BeFalse();

    [Theory]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")] // public IPv6 - refused (IPv4-only)
    [InlineData("::1")] // loopback
    [InlineData("fe80::abcd")] // link-local
    [InlineData("fc00::1")] // unique-local
    [InlineData("fd12:3456::1")] // unique-local
    [InlineData("::ffff:93.184.216.34")] // IPv4-mapped public - still refused
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64-embedded metadata - refused as IPv6
    public void IsIpAllowed_AnyIpv6_Denies(string ip) =>
        SsrfGuard.IsIpAllowed(IPAddress.Parse(ip)).Should().BeFalse();

    [Theory]
    [InlineData("172.15.255.255")] // just below 172.16/12
    [InlineData("172.32.0.0")] // just above 172.16/12
    [InlineData("100.63.255.255")] // just below 100.64/10
    [InlineData("100.128.0.0")] // just above 100.64/10
    [InlineData("192.0.1.1")] // between 192.0.0/24 and 192.0.2/24
    [InlineData("198.17.255.255")] // just below 198.18/15
    [InlineData("198.20.0.0")] // just above 198.18/15
    [InlineData("223.255.255.255")] // just below 224/3
    public void IsIpAllowed_JustOutsideBlockedRanges_Allows(string ip) =>
        SsrfGuard.IsIpAllowed(IPAddress.Parse(ip)).Should().BeTrue();

    [Theory]
    [InlineData(80)] // http
    [InlineData(443)] // https
    public void IsPortAllowed_WebPorts_Allow(int port) =>
        SsrfGuard.IsPortAllowed(port).Should().BeTrue();

    [Theory]
    [InlineData(8080)] // common alt-http / proxy
    [InlineData(8443)] // common alt-https
    [InlineData(22)] // ssh
    [InlineData(6379)] // redis
    [InlineData(5432)] // postgres
    [InlineData(0)] // unspecified
    public void IsPortAllowed_NonWebPorts_Deny(int port) =>
        SsrfGuard.IsPortAllowed(port).Should().BeFalse();

    [Fact]
    public void RedirectToPrivate_IsRejectedByUrlVet()
    {
        // The fetcher re-vets each redirect target; a hop to a private host fails the
        // same IsUrlAllowed check the initial URL passed.
        SsrfGuard.IsUrlAllowed("https://example.com/start").Should().BeTrue();
        SsrfGuard.IsUrlAllowed("http://169.254.169.254/redirected").Should().BeFalse();
    }
}
