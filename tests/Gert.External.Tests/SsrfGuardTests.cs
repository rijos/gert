using System.Net;
using FluentAssertions;
using Gert.External.Search;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// Unit tests for the SSRF policy (security F5). Pure: feed URLs/IPs, assert allow/deny;
/// no network. Covers public-allow vs private/loopback/link-local/ULA/metadata/non-http
/// scheme deny, and the redirect-to-private case via the URL/IP split (the fetcher
/// re-runs <see cref="SsrfGuard.IsUrlAllowed"/> on each redirect target).
/// </summary>
public sealed class SsrfGuardTests
{
    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("http://example.com")]
    [InlineData("https://93.184.216.34/")] // a public literal IP
    [InlineData("https://[2606:2800:220:1:248:1893:25c8:1946]/")] // public IPv6 literal
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
    [InlineData("http://[::1]/")] // IPv6 loopback
    [InlineData("http://[fe80::1]/")] // IPv6 link-local
    [InlineData("http://[fc00::1]/")] // IPv6 unique-local
    [InlineData("http://[fd00::1]/")] // IPv6 ULA
    [InlineData("http://[::ffff:127.0.0.1]/")] // IPv4-mapped loopback
    [InlineData("http://[::ffff:169.254.169.254]/")] // IPv4-mapped metadata
    public void IsUrlAllowed_PrivateOrSpecialLiteral_Denies(string url) =>
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
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    public void IsIpAllowed_Public_Allows(string ip) =>
        SsrfGuard.IsIpAllowed(IPAddress.Parse(ip)).Should().BeTrue();

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.127.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::abcd")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    public void IsIpAllowed_PrivateOrSpecial_Denies(string ip) =>
        SsrfGuard.IsIpAllowed(IPAddress.Parse(ip)).Should().BeFalse();

    [Fact]
    public void RedirectToPrivate_IsRejectedByUrlVet()
    {
        // The fetcher re-vets each redirect target; a hop to a private host fails the
        // same IsUrlAllowed check the initial URL passed.
        SsrfGuard.IsUrlAllowed("https://example.com/start").Should().BeTrue();
        SsrfGuard.IsUrlAllowed("http://169.254.169.254/redirected").Should().BeFalse();
    }
}
