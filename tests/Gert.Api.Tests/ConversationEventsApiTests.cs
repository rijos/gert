using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Gert.Model.Json;
using Gert.Testing;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// The delivery transports of the detached turn pipeline (rest-api.md
/// section receiving a turn): the range endpoint contract and the SSE stream
/// headers. Full event flow over these transports is covered by the breadth
/// tests post-cutover and the browser smoke.
/// </summary>
public sealed class ConversationEventsApiTests : IClassFixture<GertApiFactory>
{
    private static readonly JsonSerializerOptions Json = GertJsonOptions.Default;

    private readonly GertApiFactory _factory;
    private readonly string _sub = "user-" + Guid.NewGuid().ToString("N");

    public ConversationEventsApiTests(GertApiFactory factory) => _factory = factory;

    private string MintToken() => _factory.Tokens.Mint(_sub, groups: ["gert-users"], gertTools: "rag search");

    private HttpClient Authed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());
        return client;
    }

    [Fact]
    public async Task Range_requires_authentication()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/projects/default/conversations/conv-x/events");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Range_returns_an_empty_page_for_a_fresh_conversation()
    {
        var client = Authed();

        var response = await client.GetAsync("/api/projects/default/conversations/conv-x/events?after=0&limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("events").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("has_more").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("next_cursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Stream_requires_authentication()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(
            "/api/projects/default/conversations/conv-x/stream",
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Stream_opens_as_an_event_stream_and_stays_live()
    {
        var client = Authed();

        // Headers arrive immediately; the body stays open (live tail on an idle
        // conversation) - exactly the proxy-compatible SSE shape.
        using var response = await client.GetAsync(
            "/api/projects/default/conversations/conv-x/stream",
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.GetValues("X-Accel-Buffering").Should().ContainSingle("no");
    }
}
