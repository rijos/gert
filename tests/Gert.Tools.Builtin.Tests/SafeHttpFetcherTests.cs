using FluentAssertions;
using Gert.Tools.Fetch;
using Gert.Tools.Search.SearXNG;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Tools.Builtin.Tests;

/// <summary>
/// Tests the fetcher's URL-vetting boundary - the parts that reject before any socket is
/// opened (scheme + blocked literal host). The connect-time IP guard + redirect re-check
/// need a live socket and are exercised against loopback listeners in
/// <see cref="SafeHttpFetcherRedirectTests"/> (security F5); here we prove a non-http(s)
/// scheme and a private literal host are refused up-front.
/// </summary>
public sealed class SafeHttpFetcherTests
{
    private static SafeHttpFetcher NewFetcher() =>
        new(Options.Create(new SearXngOptions()), NullLogger<SafeHttpFetcher>.Instance);

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://x/")]
    [InlineData("ftp://x/")]
    public async Task FetchAsync_NonHttpScheme_Blocks(string url)
    {
        using var fetcher = NewFetcher();
        var act = () => fetcher.FetchAsync(url);
        await act.Should().ThrowAsync<SsrfBlockedException>();
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.0.0.1/")]
    public async Task FetchAsync_PrivateLiteralHost_Blocks(string url)
    {
        using var fetcher = NewFetcher();
        var act = () => fetcher.FetchAsync(url);
        await act.Should().ThrowAsync<SsrfBlockedException>();
    }

    [Theory]
    [InlineData("http://example.com:8080/")]
    [InlineData("https://example.com:8443/")]
    [InlineData("http://example.com:22/")]
    [InlineData("http://example.com:6379/")]
    public async Task FetchAsync_NonWebPort_Blocks(string url)
    {
        using var fetcher = NewFetcher();
        var act = () => fetcher.FetchAsync(url);
        (await act.Should().ThrowAsync<SsrfBlockedException>()).WithMessage("*Port blocked*");
    }

    [Fact]
    public async Task FetchAsync_MalformedUrl_Blocks()
    {
        using var fetcher = NewFetcher();
        var act = () => fetcher.FetchAsync("not a url");
        await act.Should().ThrowAsync<SsrfBlockedException>();
    }
}
