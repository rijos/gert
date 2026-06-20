using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Gert.Service.Chat;
using Gert.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// Answer a question (rest-api.md section answer a question): the HTTP answer
/// endpoint's auth + outcome contract - it actually completes a registered
/// pending question (202), a non-pending/foreign/stale question is an opaque
/// 404, a closed question's off-menu answer is a 400, and the fail-closed body
/// validation rejects malformed input before the registry is touched. The
/// route/auth/ownership shape mirrors <see cref="CancelApiTests"/>.
/// </summary>
public sealed class AnswerApiTests : IClassFixture<GertApiFactory>
{
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

    private ITurnQuestions Registry =>
        _factory.Services.GetRequiredService<ITurnQuestions>();

    private TurnKey KeyFor(string sub, string conversationId) =>
        new(_factory.Tokens.Issuer, sub, "default", conversationId);

    private static StringContent Body(string questionId, params string[] answers) =>
        new(
            $$"""{"question_id":"{{questionId}}","answers":[{{string.Join(",", answers.Select(a => $"\"{a}\""))}}]}""",
            Encoding.UTF8,
            "application/json");

    private static QuestionPayload OneQuestion(IReadOnlyList<string> options, bool allowFreeText) =>
        new([new QuestionItem("Which color?", null, options, allowFreeText)]);

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

        var response = await client.PostAsync(
            "/api/projects/default/conversations/conv-none/answer",
            Body(Guid.NewGuid().ToString("D"), "blue"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Answer_completes_the_registered_question_with_202()
    {
        using var pending = Registry.Open(
            KeyFor(_sub, "conv-live"),
            OneQuestion(["red", "blue"], allowFreeText: false));
        var wait = pending.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var client = Authed();

        var response = await client.PostAsync(
            "/api/projects/default/conversations/conv-live/answer",
            Body(pending.QuestionId, "blue"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await wait).Should().Equal("blue");
    }

    [Fact]
    public async Task An_off_menu_answer_to_a_closed_question_is_400_and_keeps_it_pending()
    {
        using var pending = Registry.Open(
            KeyFor(_sub, "conv-closed"),
            OneQuestion(["red", "blue"], allowFreeText: false));
        var wait = pending.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var client = Authed();

        var refused = await client.PostAsync(
            "/api/projects/default/conversations/conv-closed/answer",
            Body(pending.QuestionId, "green"));

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The question survived the rejected attempt and still accepts an option.
        var accepted = await client.PostAsync(
            "/api/projects/default/conversations/conv-closed/answer",
            Body(pending.QuestionId, "red"));
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await wait).Should().Equal("red");
    }

    [Fact]
    public async Task A_stale_question_id_is_an_opaque_404()
    {
        using var pending = Registry.Open(
            KeyFor(_sub, "conv-stale"),
            OneQuestion([], allowFreeText: true));
        var client = Authed();

        var response = await client.PostAsync(
            "/api/projects/default/conversations/conv-stale/answer",
            Body(Guid.NewGuid().ToString("D"), "blue"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Answer_cannot_address_another_tenants_question()
    {
        // Another user's question under the SAME conversation id: the caller's
        // key carries their own iss/sub, so the foreign question stays pending
        // and the caller sees the same 404 as none-pending (no oracle).
        using var foreign = Registry.Open(
            KeyFor("someone-else", "conv-shared-id"),
            OneQuestion([], allowFreeText: true));
        var wait = foreign.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var client = Authed();

        var response = await client.PostAsync(
            "/api/projects/default/conversations/conv-shared-id/answer",
            Body(foreign.QuestionId, "blue"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await wait).Should().BeNull("the foreign question must never receive this answer");
    }

    [Fact]
    public async Task A_count_mismatch_to_a_pending_question_is_400_and_keeps_it_pending()
    {
        // Two questions pend but the body carries one answer: a fail-closed
        // count check in the registry refuses to mis-pair them.
        using var pending = Registry.Open(
            KeyFor(_sub, "conv-count"),
            new QuestionPayload(
            [
                new QuestionItem("Which color?", null, ["red", "blue"], AllowFreeText: false),
                new QuestionItem("Anything else?", null, [], AllowFreeText: true),
            ]));
        var wait = pending.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var client = Authed();

        var refused = await client.PostAsync(
            "/api/projects/default/conversations/conv-count/answer",
            Body(pending.QuestionId, "blue"));

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The question survives and still accepts the matching count.
        var accepted = await client.PostAsync(
            "/api/projects/default/conversations/conv-count/answer",
            Body(pending.QuestionId, "blue", "no thanks"));
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await wait).Should().Equal("blue", "no thanks");
    }

    [Theory]
    [InlineData("""{"question_id":"not-a-guid","answers":["blue"]}""")]
    [InlineData("""{"question_id":"5f0c6a1e-9d4b-4c7e-8f23-0a1b2c3d4e5f","answers":["   "]}""")]
    [InlineData("""{"question_id":"5f0c6a1e-9d4b-4c7e-8f23-0a1b2c3d4e5f","answers":["a\u0007b"]}""")]
    [InlineData("""{"question_id":"5f0c6a1e-9d4b-4c7e-8f23-0a1b2c3d4e5f"}""")]
    [InlineData("""{"question_id":"5f0c6a1e-9d4b-4c7e-8f23-0a1b2c3d4e5f","answers":[]}""")]
    [InlineData("""{"question_id":"5f0c6a1e-9d4b-4c7e-8f23-0a1b2c3d4e5f","answers":["a","b","c","d","e"]}""")]
    [InlineData("not json at all")]
    public async Task Malformed_bodies_are_rejected_with_400(string body)
    {
        var client = Authed();

        var response = await client.PostAsync(
            "/api/projects/default/conversations/conv-x/answer",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_invalid_project_id_is_rejected_before_the_registry()
    {
        var client = Authed();

        var response = await client.PostAsync(
            "/api/projects/not-a-project-id/conversations/conv-x/answer",
            Body(Guid.NewGuid().ToString("D"), "blue"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
