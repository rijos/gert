using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using FluentAssertions;
using Gert.Service.Chat;
using Gert.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// Stop generation (rest-api.md § stop generation): the HTTP cancel endpoint's
/// auth + idempotency contract, that it actually signals a registered turn, the
/// tenant scoping of the key, and the WS <c>{"type":"cancel"}</c> path.
/// </summary>
public sealed class CancelApiTests : IClassFixture<GertApiFactory>
{
    private readonly GertApiFactory _factory;
    private readonly string _sub = "user-" + Guid.NewGuid().ToString("N");

    public CancelApiTests(GertApiFactory factory) => _factory = factory;

    private string MintToken() => _factory.Tokens.Mint(_sub, groups: ["gert-users"], gertTools: "rag search");

    private HttpClient Authed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());
        return client;
    }

    private ITurnCancellation Registry =>
        _factory.Services.GetRequiredService<ITurnCancellation>();

    private TurnKey KeyFor(string sub, string conversationId) =>
        new(_factory.Tokens.Issuer, sub, "default", conversationId);

    [Fact]
    public async Task Cancel_requires_authentication()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsync(
            "/api/projects/default/conversations/conv-x/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cancel_with_no_inflight_turn_is_an_idempotent_204()
    {
        var client = Authed();

        var response = await client.PostAsync(
            "/api/projects/default/conversations/conv-none/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Cancel_signals_the_registered_turn_with_202()
    {
        using var registration = Registry.Register(KeyFor(_sub, "conv-live"), CancellationToken.None);
        var client = Authed();

        var response = await client.PostAsync(
            "/api/projects/default/conversations/conv-live/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        registration.IsUserCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_cannot_address_another_tenants_turn()
    {
        // Another user's turn under the SAME conversation id: the caller's key
        // carries their own iss/sub, so the foreign registration stays untouched.
        using var foreign = Registry.Register(KeyFor("someone-else", "conv-shared-id"), CancellationToken.None);
        var client = Authed();

        var response = await client.PostAsync(
            "/api/projects/default/conversations/conv-shared-id/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        foreign.IsUserCancelled.Should().BeFalse();
    }

    [Fact]
    public async Task Ws_cancel_message_signals_the_turn_of_the_sockets_conversation()
    {
        using var registration = Registry.Register(KeyFor(_sub, "conv-ws"), CancellationToken.None);

        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request =>
            request.Headers["Sec-WebSocket-Protocol"] = $"bearer, {MintToken()}";

        var socket = await wsClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, "/api/projects/default/conversations/conv-ws/ws"),
            TestContext.Current.CancellationToken);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"cancel"}"""),
            WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

        // The handler runs on the receive loop; poll briefly for the signal.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!registration.IsUserCancelled && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        registration.IsUserCancelled.Should().BeTrue();
        socket.State.Should().Be(WebSocketState.Open, "cancel must not tear the socket down");

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, TestContext.Current.CancellationToken);
    }
}
