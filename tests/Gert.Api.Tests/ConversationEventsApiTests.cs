using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Gert.Model.Json;
using Gert.Testing;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// The delivery transports of the detached turn pipeline (rest-api.md
/// § receiving a turn): the range endpoint contract, the SSE stream headers,
/// and the WS bearer-subprotocol handshake (security F2 — the gate runs before
/// the upgrade is accepted). Full event flow over these transports is covered
/// by the breadth tests post-cutover and the browser smoke.
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

    // --- range --------------------------------------------------------------

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

    // --- SSE stream -----------------------------------------------------------

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
        // conversation) — exactly the proxy-compatible SSE shape.
        using var response = await client.GetAsync(
            "/api/projects/default/conversations/conv-x/stream",
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.GetValues("X-Accel-Buffering").Should().ContainSingle("no");
    }

    // --- WS handshake ---------------------------------------------------------

    [Fact]
    public async Task Ws_without_the_bearer_subprotocol_is_rejected_401()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();

        var act = () => wsClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, "/api/projects/default/conversations/conv-x/ws"),
            TestContext.Current.CancellationToken);

        // TestServer surfaces the failed upgrade as an exception carrying the status.
        (await act.Should().ThrowAsync<Exception>())
            .Which.Message.Should().Contain("401");
    }

    [Fact]
    public async Task Ws_with_a_garbage_token_is_rejected_401()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request =>
            request.Headers["Sec-WebSocket-Protocol"] = "bearer, not-a-jwt";

        var act = () => wsClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, "/api/projects/default/conversations/conv-x/ws"),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<Exception>())
            .Which.Message.Should().Contain("401");
    }

    [Fact]
    public async Task Ws_with_a_valid_subprotocol_token_accepts_echoes_bearer_and_serves_range()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request =>
            request.Headers["Sec-WebSocket-Protocol"] = $"bearer, {MintToken()}";

        var socket = await wsClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, "/api/projects/default/conversations/conv-x/ws"),
            TestContext.Current.CancellationToken);

        socket.State.Should().Be(WebSocketState.Open);
        socket.SubProtocol.Should().Be("bearer");

        // Drive the registry over the socket: a range request on a fresh
        // conversation returns an empty page frame.
        var request = Encoding.UTF8.GetBytes("""{"type":"range","after":0,"limit":10}""");
        await socket.SendAsync(request, WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
        result.MessageType.Should().Be(WebSocketMessageType.Text);

        using var frame = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
        frame.RootElement.GetProperty("kind").GetString().Should().Be("range");
        frame.RootElement.GetProperty("events").GetArrayLength().Should().Be(0);
        frame.RootElement.GetProperty("has_more").GetBoolean().Should().BeFalse();

        // Malformed and unknown messages must NOT tear the socket down.
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("{not json"), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"fly-to-the-moon"}"""),
            WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

        await socket.SendAsync(request, WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        var second = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
        second.MessageType.Should().Be(WebSocketMessageType.Text,
            "the socket survives hostile input and keeps serving");

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, TestContext.Current.CancellationToken);
    }
}
