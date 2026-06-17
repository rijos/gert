using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Gert.Api.Contracts;
using Gert.Chat;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Model.Json;
using Gert.Service.External;
using Gert.Testing;
using Gert.Testing.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// Race / dead-zone integration tests around switching conversations while a
/// turn streams: the breakage modes a fast user invites by submitting and
/// switching before the model finishes. Turns are PACED with real delays (a
/// custom <see cref="IChatModelClient"/>), so these are deliberately slow and
/// timing-coupled - <b>not part of the CI gate</b>: `make test` filters
/// <c>Category!=Race</c>; run them on demand with `make test-race`.
/// </summary>
[Trait("Category", "Race")]
public sealed class ConversationSwitchingRaceTests : IClassFixture<GertApiFactory>, IDisposable
{
    private static readonly JsonSerializerOptions Json = GertJsonOptions.Default;

    private readonly WebApplicationFactory<Program> _factory;

    public ConversationSwitchingRaceTests(GertApiFactory factory)
    {
        Tokens = factory;
        // Swap the instant fixture model for a paced one - runs AFTER the
        // parent's fake registration, so this replace wins.
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IChatModelClient>(new PacedChatModel()))));
    }

    private GertApiFactory Tokens { get; }

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Streams a conversation-tagged reply as ~13 deltas over ~1 s, so a test
    /// has a real window to switch/reload/abandon mid-turn. The tag is the
    /// text before the first ':' of the user message - assertions use it to
    /// prove streams never cross conversations.
    /// </summary>
    private sealed class PacedChatModel : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var content = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "?";
            var tag = content.Split(':')[0];
            yield return new ChatModelChunk { TextDelta = $"[{tag}]" };
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(80, cancellationToken);
                yield return new ChatModelChunk { TextDelta = $" d{i}" };
            }
        }
    }

    private HttpClient Authed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Tokens.TokenFor("user"));
        return client;
    }

    private async Task<string> CreateConversationAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync(
            "/api/projects/default/conversations",
            new CreateConversationRequest { Title = title },
            Json);
        var conversation = await response.Content.ReadFromJsonAsync<Conversation>(Json);
        return conversation!.Id;
    }

    private static Task<TurnAccepted?> SendAsync(HttpClient client, string cid, string content) =>
        client.PostAsJsonAsync(
                $"/api/projects/default/conversations/{cid}/messages",
                new SendMessageRequest { Content = content },
                Json)
            .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<TurnAccepted>(Json))
            .Unwrap();

    private sealed record Frame(long Seq, string Event, string Data);

    /// <summary>
    /// Read SSE frames until the terminal event (or <paramref name="maxFrames"/>,
    /// for the abandon-midway tests).
    /// </summary>
    private static async Task<List<Frame>> ReadFramesAsync(
        HttpResponseMessage response,
        int maxFrames = int.MaxValue)
    {
        var frames = new List<Frame>();
        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body, Encoding.UTF8);

        long seq = 0;
        string? eventName = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                seq = long.Parse(line[3..].Trim());
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line["data:".Length..].TrimStart());
            }
            else if (line.Length == 0 && eventName is not null)
            {
                frames.Add(new Frame(seq, eventName, data.ToString()));
                if (eventName is "message_end" or "error" || frames.Count >= maxFrames)
                {
                    return frames;
                }

                eventName = null;
                data.Clear();
            }
        }

        return frames;
    }

    private static string AssembleDeltas(IEnumerable<Frame> frames) =>
        string.Concat(frames
            .Where(f => f.Event == "delta")
            .Select(f => JsonDocument.Parse(f.Data).RootElement.GetProperty("text").GetString()));

    [Fact]
    public async Task Switching_mid_stream_completes_both_turns_without_crossing_streams()
    {
        var client = Authed();
        var a = await CreateConversationAsync(client, "race A");
        var b = await CreateConversationAsync(client, "race B");

        // Submit into A, "switch", and submit into B while A still streams -
        // different conversations ride different worker lanes.
        var acceptedA = await SendAsync(client, a, "alpha: tell me something");
        await Task.Delay(150);
        var acceptedB = await SendAsync(client, b, "beta: tell me something else");
        acceptedA.Should().NotBeNull();
        acceptedB.Should().NotBeNull();

        using var streamA = await client.GetAsync(
            $"/api/projects/default/conversations/{a}/stream?after={acceptedA!.Seq}",
            HttpCompletionOption.ResponseHeadersRead);
        using var streamB = await client.GetAsync(
            $"/api/projects/default/conversations/{b}/stream?after={acceptedB!.Seq}",
            HttpCompletionOption.ResponseHeadersRead);

        var framesA = ReadFramesAsync(streamA);
        var framesB = ReadFramesAsync(streamB);
        await Task.WhenAll(framesA, framesB);

        var textA = AssembleDeltas(framesA.Result);
        var textB = AssembleDeltas(framesB.Result);
        textA.Should().StartWith("[alpha]").And.NotContain("[beta]");
        textB.Should().StartWith("[beta]").And.NotContain("[alpha]");

        foreach (var cid in new[] { a, b })
        {
            var thread = await client.GetFromJsonAsync<ThreadResponse>(
                $"/api/projects/default/conversations/{cid}", Json);
            thread!.Messages.Single(m => m.Role == MessageRole.Assistant)
                .Status.Should().Be(MessageStatus.Complete);
        }
    }

    [Fact]
    public async Task Reopening_a_streaming_thread_reports_streaming_then_resumes_to_terminal()
    {
        var client = Authed();
        var cid = await CreateConversationAsync(client, "race reload");

        var accepted = await SendAsync(client, cid, "gamma: stream me");

        // The "switch away and back" mid-turn: the thread GET must report the
        // placeholder honestly while the worker streams on.
        await Task.Delay(200);
        var midway = await client.GetFromJsonAsync<ThreadResponse>(
            $"/api/projects/default/conversations/{cid}", Json);
        midway!.Messages.Single(m => m.Role == MessageRole.Assistant)
            .Status.Should().Be(MessageStatus.Streaming);

        // Resubscribe from the accepted cursor: replay + live tail to terminal.
        using var stream = await client.GetAsync(
            $"/api/projects/default/conversations/{cid}/stream?after={accepted!.Seq}",
            HttpCompletionOption.ResponseHeadersRead);
        var frames = await ReadFramesAsync(stream);
        frames[^1].Event.Should().Be("message_end");

        var final = await client.GetFromJsonAsync<ThreadResponse>(
            $"/api/projects/default/conversations/{cid}", Json);
        var assistant = final!.Messages.Single(m => m.Role == MessageRole.Assistant);
        assistant.Status.Should().Be(MessageStatus.Complete);
        assistant.Text.Should().Be(AssembleDeltas(frames));
    }

    [Fact]
    public async Task Abandoning_the_stream_and_resubscribing_loses_no_events()
    {
        var client = Authed();
        var cid = await CreateConversationAsync(client, "race resubscribe");

        var accepted = await SendAsync(client, cid, "delta: stream me");

        // First subscriber reads a handful of frames, then drops the transport
        // (tab switch / network blip). Generation is detached - it continues.
        List<Frame> first;
        using (var stream = await client.GetAsync(
                   $"/api/projects/default/conversations/{cid}/stream?after={accepted!.Seq}",
                   HttpCompletionOption.ResponseHeadersRead))
        {
            first = await ReadFramesAsync(stream, maxFrames: 3);
        }

        first.Should().NotBeEmpty();
        var cursor = first[^1].Seq;

        // Resubscribe from the last seen seq: the splice replays the missed
        // middle and tails live - gapless, dupe-free, terminal.
        using var resumed = await client.GetAsync(
            $"/api/projects/default/conversations/{cid}/stream?after={cursor}",
            HttpCompletionOption.ResponseHeadersRead);
        var rest = await ReadFramesAsync(resumed);

        rest[^1].Event.Should().Be("message_end");
        var seqs = first.Concat(rest).Select(f => f.Seq).ToList();
        seqs.Should().BeInAscendingOrder();
        seqs.Should().OnlyHaveUniqueItems();

        var thread = await client.GetFromJsonAsync<ThreadResponse>(
            $"/api/projects/default/conversations/{cid}", Json);
        thread!.Messages.Single(m => m.Role == MessageRole.Assistant)
            .Text.Should().Be(AssembleDeltas(first.Concat(rest)));
    }

    [Fact]
    public async Task A_second_send_into_a_streaming_conversation_409s_and_the_turn_survives()
    {
        var client = Authed();
        var cid = await CreateConversationAsync(client, "race 409");

        var accepted = await SendAsync(client, cid, "epsilon: stream me");

        var second = await client.PostAsJsonAsync(
            $"/api/projects/default/conversations/{cid}/messages",
            new SendMessageRequest { Content = "epsilon: impatient second send" },
            Json);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var stream = await client.GetAsync(
            $"/api/projects/default/conversations/{cid}/stream?after={accepted!.Seq}",
            HttpCompletionOption.ResponseHeadersRead);
        var frames = await ReadFramesAsync(stream);
        frames[^1].Event.Should().Be("message_end");

        // The rejected send persisted nothing: one user row, one assistant row.
        var thread = await client.GetFromJsonAsync<ThreadResponse>(
            $"/api/projects/default/conversations/{cid}", Json);
        thread!.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task Deleting_a_sibling_conversation_mid_stream_leaves_the_turn_alone()
    {
        var client = Authed();
        var a = await CreateConversationAsync(client, "race survivor");
        var b = await CreateConversationAsync(client, "race casualty");

        var accepted = await SendAsync(client, a, "zeta: stream me");
        await Task.Delay(150);

        // The user switches away mid-stream and deletes ANOTHER conversation.
        var delete = await client.DeleteAsync($"/api/projects/default/conversations/{b}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var stream = await client.GetAsync(
            $"/api/projects/default/conversations/{a}/stream?after={accepted!.Seq}",
            HttpCompletionOption.ResponseHeadersRead);
        var frames = await ReadFramesAsync(stream);
        frames[^1].Event.Should().Be("message_end");

        var thread = await client.GetFromJsonAsync<ThreadResponse>(
            $"/api/projects/default/conversations/{a}", Json);
        thread!.Messages.Single(m => m.Role == MessageRole.Assistant)
            .Status.Should().Be(MessageStatus.Complete);
    }
}
