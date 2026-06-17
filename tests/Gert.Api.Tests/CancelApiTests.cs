using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Gert.Service.Chat;
using Gert.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// Stop generation (rest-api.md section stop generation): the HTTP cancel endpoint's
/// auth + idempotency contract, that it actually signals a registered turn, and the
/// tenant scoping of the key.
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
}
