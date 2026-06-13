using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Gert.Api.Contracts;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Model.Json;
using Gert.Model.Projects;
using Gert.Testing;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// <c>POST .../conversations/{id}/move</c> (rest-api.md section conversations): a
/// finished thread relocates whole - conversation, messages, artifacts - into
/// another of the caller's projects; the source stops serving it. Runs over
/// real temp SQLite on both ends via <see cref="GertApiFactory"/>.
/// </summary>
public sealed class MoveConversationTests : IClassFixture<GertApiFactory>
{
    private static readonly JsonSerializerOptions Json = GertJsonOptions.Default;

    private readonly GertApiFactory _factory;

    public MoveConversationTests(GertApiFactory factory) => _factory = factory;

    private HttpClient Authed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.TokenFor("user"));
        return client;
    }

    [Fact]
    public async Task Move_relocates_a_finished_thread_and_the_source_stops_serving_it()
    {
        var client = Authed();

        // A thread with real content: the fixture turn runs to completion.
        var create = await client.PostAsJsonAsync(
            "/api/projects/default/conversations",
            new CreateConversationRequest { Title = "Movable" },
            Json);
        var conversation = await create.Content.ReadFromJsonAsync<Conversation>(Json);
        conversation.Should().NotBeNull();

        var post = await client.PostAsJsonAsync(
            $"/api/projects/default/conversations/{conversation!.Id}/messages",
            new SendMessageRequest { Content = "should I use Qdrant or sqlite-vec?" },
            Json);
        post.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilCompleteAsync(client, "default", conversation.Id);

        var project = await (await client.PostAsJsonAsync(
                "/api/projects",
                new CreateProjectRequest { Name = "Move target" },
                Json))
            .Content.ReadFromJsonAsync<ProjectMeta>(Json);
        project.Should().NotBeNull();

        var move = await client.PostAsJsonAsync(
            $"/api/projects/default/conversations/{conversation.Id}/move",
            new MoveConversationRequest { TargetPid = project!.Id },
            Json);
        move.StatusCode.Should().Be(HttpStatusCode.OK);

        // The whole thread now serves from the target...
        var movedThread = await client.GetFromJsonAsync<ThreadResponse>(
            $"/api/projects/{project.Id}/conversations/{conversation.Id}", Json);
        movedThread.Should().NotBeNull();
        movedThread!.Messages.Should().HaveCount(2);
        movedThread.Messages.Select(m => m.Role)
            .Should().Equal(MessageRole.User, MessageRole.Assistant);
        movedThread.Messages[1].Text.Should().Contain("sqlite-vec");

        // ...and the source no longer knows it.
        var sourceGet = await client.GetAsync(
            $"/api/projects/default/conversations/{conversation.Id}");
        sourceGet.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var sourceList = await client.GetFromJsonAsync<IReadOnlyList<Conversation>>(
            "/api/projects/default/conversations", Json);
        sourceList!.Select(c => c.Id).Should().NotContain(conversation.Id);
    }

    [Fact]
    public async Task Moving_a_missing_conversation_is_404()
    {
        var client = Authed();

        var move = await client.PostAsJsonAsync(
            "/api/projects/default/conversations/no-such-conversation/move",
            new MoveConversationRequest { TargetPid = "default" },
            Json);

        move.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_malformed_target_pid_is_a_400()
    {
        var client = Authed();

        var move = await client.PostAsJsonAsync(
            "/api/projects/default/conversations/whatever/move",
            new MoveConversationRequest { TargetPid = "../escape" },
            Json);

        move.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Poll the thread until its assistant row leaves <c>streaming</c>.</summary>
    private static async Task WaitUntilCompleteAsync(HttpClient client, string pid, string conversationId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var thread = await client.GetFromJsonAsync<ThreadResponse>(
                $"/api/projects/{pid}/conversations/{conversationId}", Json);
            if (thread is not null
                && thread.Messages.Any(m => m.Role == MessageRole.Assistant)
                && thread.Messages.All(m => m.Status != MessageStatus.Streaming))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("the fixture turn never finished");
    }
}
