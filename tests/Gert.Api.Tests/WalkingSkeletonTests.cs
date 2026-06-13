using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Gert.Api.Contracts;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Model.Json;
using Gert.Service.Storage;
using Gert.Testing;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// The walking skeleton: through <see cref="GertApiFactory"/>,
/// a minted JWT -> lazy provisioning -> create conversation -> POST message -> SSE
/// stream from <c>FakeChatModel</c> -> persisted, plus 401 / anonymous / SPA-fallback.
/// </summary>
public sealed class WalkingSkeletonTests : IClassFixture<GertApiFactory>
{
    private static readonly JsonSerializerOptions Json = GertJsonOptions.Default;

    private readonly GertApiFactory _factory;

    public WalkingSkeletonTests(GertApiFactory factory) => _factory = factory;

    private HttpClient Authed(string role = "user")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.TokenFor(role));
        return client;
    }

    [Fact]
    public async Task No_token_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/projects/default/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Healthz_is_anonymous_and_returns_200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task First_authenticated_call_lazily_provisions_the_user_folder()
    {
        var client = Authed();

        var response = await client.GetAsync("/api/projects/default/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The token's (iss, sub) -> folder key. The standing "user" role mints sub="dev-user".
        var key = StorageKeys.UserKey(_factory.Tokens.Issuer, "dev-user");
        var userRoot = Path.Combine(_factory.UsersDir, key);
        var chatDb = Path.Combine(userRoot, "projects", "default", "chat.db");

        Directory.Exists(userRoot).Should().BeTrue("the user folder is created on first authenticated touch");
        Directory.Exists(Path.Combine(userRoot, "projects", "default"))
            .Should().BeTrue("the default project is created lazily");
        File.Exists(chatDb).Should().BeTrue("chat.db is provisioned + migrated on OpenChatAsync");
    }

    [Fact]
    public async Task Create_then_list_conversation_round_trips()
    {
        var client = Authed();

        var create = await client.PostAsJsonAsync(
            "/api/projects/default/conversations",
            new CreateConversationRequest { Title = "Round trip" },
            Json);

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<Conversation>(Json);
        created.Should().NotBeNull();
        created!.Title.Should().Be("Round trip");

        var list = await client.GetFromJsonAsync<IReadOnlyList<Conversation>>(
            "/api/projects/default/conversations", Json);

        list.Should().NotBeNull();
        list!.Select(c => c.Id).Should().Contain(created.Id);
    }

    [Fact]
    public async Task Detached_turn_accepts_then_streams_message_start_deltas_message_end_and_persists()
    {
        var client = Authed();

        // Create the conversation to post into.
        var createResponse = await client.PostAsJsonAsync(
            "/api/projects/default/conversations",
            new CreateConversationRequest { Title = "Qdrant?" },
            Json);
        var conversation = await createResponse.Content.ReadFromJsonAsync<Conversation>(Json);
        conversation.Should().NotBeNull();

        // POST the fixture-keyed message: 202 + the ids and the subscribe cursor
        // (the turn itself runs detached on the worker).
        var post = await client.PostAsJsonAsync(
            $"/api/projects/default/conversations/{conversation!.Id}/messages",
            new SendMessageRequest { Content = "should I use Qdrant or sqlite-vec?" },
            Json);

        post.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var accepted = await post.Content.ReadFromJsonAsync<TurnAccepted>(Json);
        accepted.Should().NotBeNull();
        accepted!.AssistantMessageId.Should().NotBeNullOrEmpty();
        accepted.Seq.Should().BeGreaterThan(0);

        // Subscribe from the cursor and read live SSE until the terminal event -
        // the splice replays anything the worker already produced, then tails.
        using var stream = await client.GetAsync(
            $"/api/projects/default/conversations/{conversation.Id}/stream?after={accepted.Seq}",
            HttpCompletionOption.ResponseHeadersRead);
        stream.StatusCode.Should().Be(HttpStatusCode.OK);
        stream.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var events = ParseSse(await ReadUntilTerminalAsync(stream));

        // The SSE event shape: message_start -> delta... -> message_end.
        events.Should().NotBeEmpty();
        events[0].Should().BeOfType<MessageStartEvent>();
        ((MessageStartEvent)events[0]).MessageId.Should().Be(accepted.AssistantMessageId);
        events[^1].Should().BeOfType<MessageEndEvent>();
        events.Skip(1).Take(events.Count - 2).Should().AllBeOfType<DeltaEvent>();

        var assembled = string.Concat(events.OfType<DeltaEvent>().Select(d => d.Text));
        assembled.Should().Be("Short version: use sqlite-vec for a homelab at this scale.");

        // The assistant message persisted (status=complete) - a follow-up GET of
        // the thread reproduces it.
        var thread = await client.GetFromJsonAsync<ThreadResponse>(
            $"/api/projects/default/conversations/{conversation.Id}", Json);

        thread.Should().NotBeNull();
        var assistant = thread!.Messages.SingleOrDefault(m => m.Role == MessageRole.Assistant);
        assistant.Should().NotBeNull();
        assistant!.Text.Should().Be("Short version: use sqlite-vec for a homelab at this scale.");
        assistant.Status.Should().Be(MessageStatus.Complete);
        assistant.Id.Should().Be(accepted.AssistantMessageId);
    }

    [Fact]
    public async Task Reasoning_turn_streams_thinking_then_answer_with_metrics_and_persists()
    {
        var client = Authed();

        var createResponse = await client.PostAsJsonAsync(
            "/api/projects/default/conversations",
            new CreateConversationRequest { Title = "Thinking" },
            Json);
        var conversation = await createResponse.Content.ReadFromJsonAsync<Conversation>(Json);

        // The reasoning fixture - the fake streams its scripted thinking deltas
        // unconditionally (response-side reasoning is always surfaced now).
        var post = await client.PostAsJsonAsync(
            $"/api/projects/default/conversations/{conversation!.Id}/messages",
            new SendMessageRequest { Content = "think about the best vector store" },
            Json);
        var accepted = await post.Content.ReadFromJsonAsync<TurnAccepted>(Json);

        using var stream = await client.GetAsync(
            $"/api/projects/default/conversations/{conversation.Id}/stream?after={accepted!.Seq}",
            HttpCompletionOption.ResponseHeadersRead);
        var events = ParseSse(await ReadUntilTerminalAsync(stream));

        // message_start -> reasoning... -> delta... -> message_end, thinking first.
        events[0].Should().BeOfType<MessageStartEvent>();
        var types = events.Select(e => e.GetType().Name).ToList();
        events.OfType<ReasoningEvent>().Should().NotBeEmpty();
        types.LastIndexOf(nameof(ReasoningEvent))
            .Should().BeLessThan(types.IndexOf(nameof(DeltaEvent)), "thinking precedes the answer");

        string.Concat(events.OfType<ReasoningEvent>().Select(r => r.Text))
            .Should().Be("The user runs a homelab. Scale is ~20 users, so operational simplicity dominates recall.");

        // The terminal event carries the generation metrics.
        var end = events[^1].Should().BeOfType<MessageEndEvent>().Subject;
        end.TokenCount.Should().Be(12);
        end.DurationMs.Should().NotBeNull();
        end.ContextTokens.Should().NotBeNull("the fake reports prompt tokens");
        end.ContextTokens.Should().BeGreaterThan(12, "context = prompt + completion");

        // Reload: the thread restores the thinking block + metrics.
        var thread = await client.GetFromJsonAsync<ThreadResponse>(
            $"/api/projects/default/conversations/{conversation.Id}", Json);
        var assistant = thread!.Messages.Single(m => m.Role == MessageRole.Assistant);
        assistant.Reasoning.Should().Contain("operational simplicity");
        assistant.TokenCount.Should().Be(12);
        assistant.ContextTokens.Should().Be(end.ContextTokens);
    }

    /// <summary>
    /// Read SSE text until the terminal frame (<c>message_end</c>/<c>error</c>) -
    /// the live stream never ends on its own (detached turns keep it open).
    /// </summary>
    private static async Task<string> ReadUntilTerminalAsync(HttpResponseMessage response)
    {
        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body, Encoding.UTF8);
        var collected = new StringBuilder();

        while (await reader.ReadLineAsync() is { } line)
        {
            collected.Append(line).Append('\n');
            if (line is "event: message_end" or "event: error")
            {
                // One more blank-line-terminated data line completes the frame.
                while (await reader.ReadLineAsync() is { } tail)
                {
                    collected.Append(tail).Append('\n');
                    if (tail.Length == 0)
                    {
                        return collected.ToString();
                    }
                }
            }
        }

        return collected.ToString();
    }

    [Fact]
    public async Task Spa_fallback_serves_index_for_client_routes_but_not_api_or_healthz()
    {
        var client = _factory.CreateClient();

        var clientRoute = await client.GetAsync("/some/client/route");
        clientRoute.StatusCode.Should().Be(HttpStatusCode.OK);
        clientRoute.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await clientRoute.Content.ReadAsStringAsync()).Should().Contain("Gert");

        // /api/* must not be swallowed by the SPA fallback.
        var unknownApi = await client.GetAsync("/api/unknown");
        unknownApi.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // /healthz stays the anonymous probe, not the SPA shell.
        var healthz = await client.GetAsync("/healthz");
        healthz.StatusCode.Should().Be(HttpStatusCode.OK);
        healthz.Content.Headers.ContentType!.MediaType.Should().NotBe("text/html");
    }

    /// <summary>
    /// Parse an SSE body (<c>event: &lt;name&gt;\ndata: &lt;json&gt;\n\n</c> frames)
    /// back into the <see cref="ChatEvent"/> union. The <c>data:</c> JSON carries the
    /// polymorphic <c>type</c> discriminator, so it round-trips through STJ.
    /// </summary>
    private static List<ChatEvent> ParseSse(string body)
    {
        var events = new List<ChatEvent>();
        var normalized = body.Replace("\r\n", "\n");

        foreach (var block in normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? eventName = null;
            var data = new StringBuilder();

            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = line["event:".Length..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(line["data:".Length..].TrimStart());
                }
            }

            if (eventName is null || data.Length == 0)
            {
                continue;
            }

            var evt = JsonSerializer.Deserialize<ChatEvent>(data.ToString(), Json);
            evt.Should().NotBeNull();
            evt!.Type.ToWireName().Should().Be(eventName);
            events.Add(evt);
        }

        return events;
    }
}
