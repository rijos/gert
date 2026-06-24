using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Gert.Model.Events;
using Gert.Storage;
using Gert.Testing;
using Gert.TurnControl;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// Answer a question (rest-api.md section answer a question): the HTTP answer endpoint's auth +
/// outcome contract over the <see cref="ITurnControlBus"/>. The endpoint submits the body to the
/// turn's control channel, which validates it against the open question - a matching id + valid
/// answer is delivered to the waiting turn (202), an off-menu / count-mismatched answer is a 400 (the
/// question stays open), a non-pending/foreign/stale question id is an opaque 404, and the fail-closed
/// body validation rejects malformed input with 400 before the bus is touched. The scope's user key is
/// token-derived, so a request keyed by a conversation id only ever reaches the caller's own turn.
/// </summary>
public sealed class AnswerApiTests : IClassFixture<GertApiFactory>
{
    private const string Pid = "default";

    private readonly GertApiFactory _factory;
    private readonly string _sub = "user-" + Guid.NewGuid().ToString("N");

    public AnswerApiTests(GertApiFactory factory) => _factory = factory;

    private HttpClient Authed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Tokens.Mint(_sub, groups: ["gert-users"], gertTools: "rag search ask_user"));
        return client;
    }

    private ITurnControlBus Bus => _factory.Services.GetRequiredService<ITurnControlBus>();

    private ControlScope ScopeFor(string sub, string conversationId) =>
        new(StorageKeys.UserKey(_factory.Tokens.Issuer, sub), Pid, conversationId);

    private static StringContent Body(string questionId, params string[] answers) =>
        new(
            $$"""{"question_id":"{{questionId}}","answers":[{{string.Join(",", answers.Select(a => $"\"{a}\""))}}]}""",
            Encoding.UTF8,
            "application/json");

    private static AskedQuestion OneClosed(params string[] options) =>
        new("Which color?", null, options, AllowFreeText: false);

    [Fact]
    public async Task Answer_requires_authentication()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsync(
            "/api/projects/default/conversations/conv-x/answer",
            Body(Guid.NewGuid().ToString("D"), "blue"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Answer_with_no_pending_question_is_404()
    {
        var client = Authed();

        // No turn subscribed for this conversation: the bus finds no open question, opaque 404.
        var response = await client.PostAsync(
            "/api/projects/default/conversations/conv-none/answer",
            Body(Guid.NewGuid().ToString("D"), "blue"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Answer_matching_the_pending_question_is_202_and_is_delivered_to_the_turn()
    {
        var conv = Guid.NewGuid().ToString("D");
        var questionId = Guid.NewGuid().ToString("D");
        await using var turn = await Bus.SubscribeAsync(ScopeFor(_sub, conv), DateTimeOffset.UtcNow);
        await turn.OpenQuestionAsync(questionId, [OneClosed("red", "blue")]);

        var response = await Authed().PostAsync(
            $"/api/projects/default/conversations/{conv}/answer",
            Body(questionId, "blue"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // The waiting turn received exactly the submitted answer.
        var delivered = await turn.WaitForAnswerAsync(questionId).WaitAsync(TimeSpan.FromSeconds(2));
        delivered.Should().Equal("blue");
    }

    [Fact]
    public async Task An_off_menu_answer_to_a_closed_question_is_400_and_keeps_it_pending()
    {
        var conv = Guid.NewGuid().ToString("D");
        var questionId = Guid.NewGuid().ToString("D");
        await using var turn = await Bus.SubscribeAsync(ScopeFor(_sub, conv), DateTimeOffset.UtcNow);
        await turn.OpenQuestionAsync(questionId, [OneClosed("red", "blue")]);
        var client = Authed();

        var refused = await client.PostAsync(
            $"/api/projects/default/conversations/{conv}/answer",
            Body(questionId, "green"));

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The question survived the rejected attempt and still accepts an offered option.
        var accepted = await client.PostAsync(
            $"/api/projects/default/conversations/{conv}/answer",
            Body(questionId, "red"));
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await turn.WaitForAnswerAsync(questionId).WaitAsync(TimeSpan.FromSeconds(2))).Should().Equal("red");
    }

    [Fact]
    public async Task A_count_mismatch_to_a_pending_question_is_400_and_keeps_it_pending()
    {
        // Two questions pend but the body carries one answer: the fit check refuses to mis-pair them.
        var conv = Guid.NewGuid().ToString("D");
        var questionId = Guid.NewGuid().ToString("D");
        await using var turn = await Bus.SubscribeAsync(ScopeFor(_sub, conv), DateTimeOffset.UtcNow);
        await turn.OpenQuestionAsync(
            questionId,
            [
                OneClosed("red", "blue"),
                new AskedQuestion("Anything else?", null, [], AllowFreeText: true),
            ]);
        var client = Authed();

        var refused = await client.PostAsync(
            $"/api/projects/default/conversations/{conv}/answer",
            Body(questionId, "blue"));

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The question survives and still accepts the matching count.
        var accepted = await client.PostAsync(
            $"/api/projects/default/conversations/{conv}/answer",
            Body(questionId, "blue", "no thanks"));
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task A_stale_question_id_is_an_opaque_404()
    {
        var conv = Guid.NewGuid().ToString("D");
        var questionId = Guid.NewGuid().ToString("D");
        await using var turn = await Bus.SubscribeAsync(ScopeFor(_sub, conv), DateTimeOffset.UtcNow);
        await turn.OpenQuestionAsync(questionId, [new AskedQuestion("anything?", null, [], AllowFreeText: true)]);

        // A question is open, but the body names a different (stale) id - 404, the same response as
        // none-pending (no oracle).
        var response = await Authed().PostAsync(
            $"/api/projects/default/conversations/{conv}/answer",
            Body(Guid.NewGuid().ToString("D"), "blue"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Answer_only_ever_reaches_the_callers_own_turn()
    {
        // A foreign tenant has a pending question under this conversation id; the caller has none. The
        // caller's token derives THEIR user key, so the scope addresses no open question -> 404, and the
        // foreign question stays pending (reachable only under the foreign scope).
        var foreignSub = "user-" + Guid.NewGuid().ToString("N");
        var conv = Guid.NewGuid().ToString("D");
        var questionId = Guid.NewGuid().ToString("D");
        await using var foreign = await Bus.SubscribeAsync(ScopeFor(foreignSub, conv), DateTimeOffset.UtcNow);
        await foreign.OpenQuestionAsync(questionId, [new AskedQuestion("anything?", null, [], AllowFreeText: true)]);

        var response = await Authed().PostAsync(
            $"/api/projects/default/conversations/{conv}/answer",
            Body(questionId, "blue"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The foreign question stayed pending: it still accepts an answer under the foreign scope.
        (await Bus.SubmitAnswerAsync(ScopeFor(foreignSub, conv), questionId, ["blue"]))
            .Should().Be(AnswerOutcome.Accepted);
    }

    [Theory]
    [InlineData("""{"question_id":"not-a-guid","answers":["blue"]}""")]
    [InlineData("""{"question_id":"5f0c6a1e-9d4b-4c7e-8f23-0a1b2c3d4e5f","answers":["   "]}""")]
    [InlineData("""{"question_id":"5f0c6a1e-9d4b-4c7e-8f23-0a1b2c3d4e5f"}""")]
    [InlineData("""{"question_id":"5f0c6a1e-9d4b-4c7e-8f23-0a1b2c3d4e5f","answers":[]}""")]
    [InlineData("""{"question_id":"5f0c6a1e-9d4b-4c7e-8f23-0a1b2c3d4e5f","answers":["a","b","c","d","e"]}""")]
    [InlineData("not json at all")]
    public async Task Malformed_bodies_are_rejected_with_400(string body)
    {
        var client = Authed();

        // The body is proven before the bus is touched - a malformed body 400s with no control-plane
        // access at all (conv-x need not name a live turn).
        var response = await client.PostAsync(
            "/api/projects/default/conversations/conv-x/answer",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_invalid_project_id_is_rejected_before_the_bus()
    {
        var client = Authed();

        var response = await client.PostAsync(
            "/api/projects/not-a-project-id/conversations/conv-x/answer",
            Body(Guid.NewGuid().ToString("D"), "blue"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
