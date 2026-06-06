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
using Gert.Testing;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// Artifact extraction end-to-end over the mocks, one test per
/// <see cref="ArtifactKind"/> the fixtures script (html / py / md): POST the
/// fixture prompt → FakeChatModel streams a named fence split across deltas →
/// TurnRunner extracts + persists → the SSE carries an <c>artifact</c> event →
/// the canvas read APIs (conversation list + artifact by id) and the thread GET
/// reproduce it. The md fixture also carries an UNNAMED fence to prove ordinary
/// code blocks stay inline.
/// </summary>
public sealed class ArtifactsE2ETests : IClassFixture<GertApiFactory>
{
    private static readonly JsonSerializerOptions Json = GertJsonOptions.Default;

    private readonly GertApiFactory _factory;

    public ArtifactsE2ETests(GertApiFactory factory) => _factory = factory;

    private HttpClient Authed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.TokenFor("user"));
        return client;
    }

    public static TheoryData<string, ArtifactKind, string, string, string> Cases => new()
    {
        // prompt → kind, name, fence language, a content marker that must survive
        { "make me a demo html page", ArtifactKind.Html, "demo.html", "html", "<h1>Demo</h1>" },
        { "write a python fibonacci script", ArtifactKind.Py, "fib.py", "python", "def fib(n):" },
        { "draft a markdown decision doc", ArtifactKind.Md, "decision.md", "md", "**sqlite-vec**" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Named_fence_becomes_a_canvas_artifact_end_to_end(
        string prompt,
        ArtifactKind kind,
        string name,
        string language,
        string contentMarker)
    {
        var client = Authed();

        var createResponse = await client.PostAsJsonAsync(
            "/api/projects/default/conversations",
            new CreateConversationRequest { Title = $"Artifacts · {name}" },
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
        var events = ParseSse(await ReadUntilTerminalAsync(stream));

        // Exactly ONE artifact event (the md fixture's unnamed fence stays
        // inline), emitted after the text, before message_end.
        var artifactEvent = events.OfType<ArtifactEvent>().Should().ContainSingle().Subject;
        artifactEvent.Kind.Should().Be(kind);
        artifactEvent.Name.Should().Be(name);
        artifactEvent.Content.Should().Contain(contentMarker);
        var types = events.Select(e => e.GetType().Name).ToList();
        types.IndexOf(nameof(ArtifactEvent))
            .Should().BeGreaterThan(types.LastIndexOf(nameof(DeltaEvent)))
            .And.BeLessThan(types.IndexOf(nameof(MessageEndEvent)));

        // Canvas list API: the conversation's artifacts carry the persisted row.
        var listed = await client.GetFromJsonAsync<IReadOnlyList<Artifact>>(
            $"/api/projects/default/conversations/{conversation.Id}/artifacts", Json);
        var row = listed.Should().ContainSingle().Subject;
        row.Id.Should().Be(artifactEvent.Id);
        row.Kind.Should().Be(kind);
        row.Name.Should().Be(name);
        row.Language.Should().Be(language);
        row.MessageId.Should().Be(accepted.AssistantMessageId);

        // Artifact-by-id API: full content round-trips.
        var fetched = await client.GetFromJsonAsync<Artifact>(
            $"/api/projects/default/artifacts/{row.Id}", Json);
        fetched!.Content.Should().Be(artifactEvent.Content);
        fetched.Content.Should().Contain(contentMarker);

        // Thread GET (the reload path) carries the artifact for the canvas strip.
        var thread = await client.GetFromJsonAsync<ThreadResponse>(
            $"/api/projects/default/conversations/{conversation.Id}", Json);
        thread!.Artifacts.Should().ContainSingle(a => a.Id == row.Id && a.Name == name);

        // And the fence body never leaks the name= token into the artifact.
        artifactEvent.Content.Should().NotContain("name=");
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
