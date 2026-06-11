using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Gert.Api.Contracts;
using Gert.Api.Controllers;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Model.Json;
using Gert.Model.Rag;
using Gert.Testing;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// The two outward-writing tools end-to-end over the mocks: <c>web_fetch</c>
/// (fixture page → content + web citation; the blocked metadata URL → a
/// card-visible TOOL ERROR while the turn completes — the F5 visibility proof)
/// and <c>save_memory</c> (fixture prompt → a <c>kind='memory'</c> entry the
/// knowledge-panel GET lists). FakeWebFetcher / the real MemoryService over
/// FakeEmbeddings stand in for the outside world (testing.md §4.2).
/// </summary>
public sealed class FetchAndMemoryToolsE2ETests : IClassFixture<GertApiFactory>
{
    private static readonly JsonSerializerOptions Json = GertJsonOptions.Default;

    private readonly GertApiFactory _factory;

    public FetchAndMemoryToolsE2ETests(GertApiFactory factory) => _factory = factory;

    private HttpClient Authed()
    {
        var client = _factory.CreateClient();
        // The standing `user` role carries "… fetch memory" — the new grants.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.TokenFor("user"));
        return client;
    }

    private async Task<List<ChatEvent>> RunTurnAsync(HttpClient client, string title, string prompt, ToolToggles tools)
    {
        var createResponse = await client.PostAsJsonAsync(
            "/api/projects/default/conversations",
            new CreateConversationRequest { Title = title, Tools = tools },
            Json);
        var conversation = await createResponse.Content.ReadFromJsonAsync<Conversation>(Json);
        conversation.Should().NotBeNull();

        var post = await client.PostAsJsonAsync(
            $"/api/projects/default/conversations/{conversation!.Id}/messages",
            new SendMessageRequest { Content = prompt },
            Json);
        post.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var accepted = await post.Content.ReadFromJsonAsync<TurnAccepted>(Json);

        using var stream = await client.GetAsync(
            $"/api/projects/default/conversations/{conversation.Id}/stream?after={accepted!.Seq}",
            HttpCompletionOption.ResponseHeadersRead);
        return ParseSse(await ReadUntilTerminalAsync(stream));
    }

    [Fact]
    public async Task Web_fetch_returns_the_fixture_page_with_a_web_citation()
    {
        var client = Authed();

        var events = await RunTurnAsync(
            client,
            "Fetch · happy",
            "fetch the sqlite-vec page",
            new ToolToggles(new Dictionary<string, bool> { ["fetch"] = true }));

        var call = events.OfType<ToolCallEvent>().Should().ContainSingle().Subject;
        call.Kind.Should().Be("fetch");

        var result = events.OfType<ToolResultEvent>().Should().ContainSingle().Subject;
        result.Kind.Should().Be("fetch");
        result.Status.Should().Be(ToolCallStatus.Done);
        result.Error.Should().BeNull();
        result.Stdout.Should().StartWith("fetched https://example.test/sqlite-vec");

        var citation = events.OfType<CitationEvent>().Should().ContainSingle().Subject;
        citation.Locator.Should().Be("https://example.test/sqlite-vec");

        events[^1].Should().BeOfType<MessageEndEvent>();
        string.Concat(events.OfType<DeltaEvent>().Select(d => d.Text))
            .Should().Contain("runs anywhere SQLite does");
    }

    [Fact]
    public async Task A_blocked_fetch_is_a_visible_tool_error_and_the_turn_still_completes()
    {
        var client = Authed();

        var events = await RunTurnAsync(
            client,
            "Fetch · blocked",
            "fetch the metadata service",
            new ToolToggles(new Dictionary<string, bool> { ["fetch"] = true }));

        // The F5 visibility proof: the refusal is a card error the model reads,
        // never a torn-down turn.
        var result = events.OfType<ToolResultEvent>().Should().ContainSingle().Subject;
        result.Kind.Should().Be("fetch");
        result.Status.Should().Be(ToolCallStatus.Error);
        result.Error.Should().Be("URL blocked by fetch policy");

        events.OfType<CitationEvent>().Should().BeEmpty();
        events[^1].Should().BeOfType<MessageEndEvent>();
        string.Concat(events.OfType<DeltaEvent>().Select(d => d.Text))
            .Should().Contain("refused by policy");
    }

    [Fact]
    public async Task Save_memory_persists_an_unpinned_entry_the_knowledge_panel_lists()
    {
        var client = Authed();

        var events = await RunTurnAsync(
            client,
            "Memory · save",
            "remember that I prefer tabs",
            new ToolToggles(new Dictionary<string, bool> { ["memory"] = true }));

        var result = events.OfType<ToolResultEvent>().Should().ContainSingle().Subject;
        result.Kind.Should().Be("memory");
        result.Status.Should().Be(ToolCallStatus.Done);
        result.Stdout.Should().Be("saved memory: Editor preference");
        events[^1].Should().BeOfType<MessageEndEvent>();

        // The write side is real (MemoryService + FakeEmbeddings): the entry is
        // listed by the knowledge-panel GET, decoded title and all, unpinned.
        var entries = await client.GetFromJsonAsync<IReadOnlyList<MemoryEntry>>(
            "/api/projects/default/memory", Json);
        var entry = entries.Should().ContainSingle(e => e.Title == "Editor preference").Subject;
        entry.Pinned.Should().BeFalse("pinning stays a human action in the knowledge panel");
    }

    // --- SSE helpers (same shape as WalkingSkeletonTests) ---------------------

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

    private static List<ChatEvent> ParseSse(string body)
    {
        var events = new List<ChatEvent>();
        var normalized = body.Replace("\r\n", "\n");

        foreach (var block in normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var data = new StringBuilder();

            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(line["data:".Length..].TrimStart());
                }
            }

            if (data.Length > 0)
            {
                events.Add(JsonSerializer.Deserialize<ChatEvent>(data.ToString(), Json)!);
            }
        }

        return events;
    }
}
