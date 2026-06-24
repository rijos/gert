using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Gert.Storage;
using Gert.Testing;
using Gert.TurnControl;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// Stop generation (rest-api.md section stop generation): the HTTP cancel endpoint's auth contract
/// and the fire-and-forget publish to the turn's control channel - always 202, the signal reaching a
/// live subscription (or no-op when none) - plus the tenant scoping the token-derived
/// <see cref="ControlScope"/> enforces: a cancel keyed by a conversation id only ever trips the
/// caller's own turn, never another tenant's turn sharing that id.
/// </summary>
public sealed class CancelApiTests : IClassFixture<GertApiFactory>
{
    private const string Pid = "default";

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

    // The one process-wide control plane the endpoint publishes to and the runner subscribes to - the
    // test stands in for a running turn by subscribing under the same scope.
    private ITurnControlBus Bus => _factory.Services.GetRequiredService<ITurnControlBus>();

    private ControlScope ScopeFor(string sub, string conversationId) =>
        new(StorageKeys.UserKey(_factory.Tokens.Issuer, sub), Pid, conversationId);

    [Fact]
    public async Task Cancel_requires_authentication()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsync(
            "/api/projects/default/conversations/conv-x/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cancel_with_nothing_in_flight_is_a_fire_and_forget_202()
    {
        // No live turn for this conversation: the publish reaches no subscription and is dropped, but
        // the endpoint is fire-and-forget, so it still answers 202 (and is idempotent).
        var client = Authed();
        var conv = Guid.NewGuid().ToString("D");

        var first = await client.PostAsync($"/api/projects/default/conversations/{conv}/cancel", null);
        var second = await client.PostAsync($"/api/projects/default/conversations/{conv}/cancel", null);

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Cancel_of_a_live_turn_trips_its_control_subscription()
    {
        var conv = Guid.NewGuid().ToString("D");
        await using var turn = await Bus.SubscribeAsync(ScopeFor(_sub, conv), DateTimeOffset.UtcNow);

        var response = await Authed().PostAsync($"/api/projects/default/conversations/{conv}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        // The publish reached the live subscription (in-process, synchronous): its token is tripped.
        turn.Cancelled.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_only_trips_the_callers_own_turn_not_a_foreign_tenants()
    {
        // The SAME conversation id is live in two tenants. The caller's token derives THEIR user key,
        // so the scope addresses only the caller's turn - the foreign tenant's turn (a different user
        // key, same conversation id) is never reached. A conversation id is not an authz boundary; the
        // token-derived user key is.
        var foreignSub = "user-" + Guid.NewGuid().ToString("N");
        var conv = Guid.NewGuid().ToString("D");
        await using var foreign = await Bus.SubscribeAsync(ScopeFor(foreignSub, conv), DateTimeOffset.UtcNow);
        await using var caller = await Bus.SubscribeAsync(ScopeFor(_sub, conv), DateTimeOffset.UtcNow);

        var response = await Authed().PostAsync($"/api/projects/default/conversations/{conv}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        caller.Cancelled.IsCancellationRequested.Should().BeTrue("the caller's own turn is cancelled");
        foreign.Cancelled.IsCancellationRequested.Should()
            .BeFalse("a foreign tenant's turn with the same conversation id is never reached");
    }
}
