using System.Net.Http.Headers;
using FluentAssertions;
using Gert.Api.Security;
using Gert.Testing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// Fail-closed cache control (principles.md #1): authenticated, per-user responses must
/// never be cacheable by an intermediary (the Caddy edge, a CDN, a corporate proxy), or
/// one user's data could be served to another. The meta-assertion is that
/// <see cref="NoStoreByDefaultFilter"/> is a GLOBAL MVC filter - so the guarantee holds for
/// every controller by construction, not just the endpoints sampled here - backed by live
/// checks that the header actually lands on the wire.
/// </summary>
public sealed class CacheControlMetaTests : IClassFixture<GertApiFactory>
{
    private readonly GertApiFactory _factory;

    public CacheControlMetaTests(GertApiFactory factory) => _factory = factory;

    private HttpClient Authed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.TokenFor("user"));
        return client;
    }

    [Fact]
    public void NoStore_filter_is_registered_globally_so_every_controller_is_covered()
    {
        var mvcOptions = _factory.Services.GetRequiredService<IOptions<MvcOptions>>().Value;

        // Filters.Add<T>() records a TypeFilterAttribute whose ImplementationType is T.
        mvcOptions.Filters
            .OfType<TypeFilterAttribute>()
            .Select(f => f.ImplementationType)
            .Should().Contain(typeof(NoStoreByDefaultFilter));
    }

    [Theory]
    [InlineData("/api/models")]
    [InlineData("/api/settings")]
    [InlineData("/api/projects")]
    public async Task Authenticated_GET_responses_are_no_store(string path)
    {
        using var client = Authed();

        var response = await client.GetAsync(path);

        response.IsSuccessStatusCode.Should().BeTrue($"{path} should be reachable for an authed user");
        response.Headers.CacheControl.Should().NotBeNull($"{path} must carry a Cache-Control header");
        response.Headers.CacheControl!.NoStore.Should().BeTrue($"{path} must be Cache-Control: no-store");
    }

    [Fact]
    public async Task Client_route_shell_revalidates_so_a_deploy_is_never_masked()
    {
        // The SPA fallback serves index.html for a client route, OUTSIDE the static-file
        // middleware - SecurityHeadersMiddleware stamps the revalidation header on text/html.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/some/client/route");

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        response.Headers.CacheControl!.NoCache.Should().BeTrue("the shell must revalidate, not be cached stale");
    }

    [Fact]
    public async Task Static_asset_revalidates_via_the_static_file_pipeline()
    {
        // favicon.svg is served directly by the static-file middleware -> OnPrepareResponse.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/favicon.svg");

        response.IsSuccessStatusCode.Should().BeTrue();
        response.Headers.CacheControl!.NoCache.Should().BeTrue("stable-named assets revalidate via ETag");
    }
}
