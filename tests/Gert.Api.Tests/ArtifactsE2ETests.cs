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
using Gert.Testing;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// Artifact creation end-to-end over the mocks, one test per
/// <see cref="ArtifactKind"/> the fixtures script (html / py / md): POST the
/// fixture prompt -> FakeChatModel calls <c>make_artifact</c> -> the tool persists
/// the file and TurnRunner emits an <c>artifact</c> event -> the canvas read APIs
/// (conversation list + artifact by id) and the thread GET reproduce it.
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

    /// <summary>The canvas suite, enabled on the conversation so the planner offers it.</summary>
    private static ToolToggles ArtifactTools => new(new Dictionary<string, bool>
    {
        ["make_artifact"] = true,
        ["edit_artifact"] = true,
        ["read_artifact"] = true,
    });

    public static TheoryData<string, ArtifactKind, string, string, string> Cases => new()
    {
        // prompt -> kind, name, stored language, a content marker that must survive
        { "make me a demo html page", ArtifactKind.Html, "demo.html", "html", "<h1>Demo</h1>" },
        { "write a python fibonacci script", ArtifactKind.Py, "fib.py", "python", "def fib(n):" },
        { "draft a markdown decision doc", ArtifactKind.Md, "decision.md", "markdown", "**sqlite-vec**" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Make_artifact_tool_creates_a_canvas_artifact_end_to_end(
        string prompt,
        ArtifactKind kind,
        string name,
        string language,
        string contentMarker)
    {
        var client = Authed();

        var createResponse = await client.PostAsJsonAsync(
            "/api/projects/default/conversations",
            new CreateConversationRequest { Title = $"Artifacts - {name}", Tools = ArtifactTools },
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

        // Exactly ONE artifact event, emitted from the tool loop: after the
        // tool_result that produced it, before message_end.
        var artifactEvent = events.OfType<ArtifactEvent>().Should().ContainSingle().Subject;
        artifactEvent.Kind.Should().Be(kind);
        artifactEvent.Name.Should().Be(name);
        artifactEvent.Content.Should().Contain(contentMarker);
        var types = events.Select(e => e.GetType().Name).ToList();
        types.IndexOf(nameof(ArtifactEvent))
            .Should().BeGreaterThan(types.IndexOf(nameof(ToolResultEvent)))
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
    }

    [Fact]
    public async Task Html_artifact_renders_via_signed_ticket_on_the_raw_endpoint()
    {
        var client = Authed();
        var artifactId = await SeedHtmlArtifactAsync(client);

        // 1) Authed, pid-scoped mint -> a signed render URL (origin-relative here,
        //    since no Artifacts:Origin is configured in Testing).
        var ticket = await client.GetFromJsonAsync<ArtifactTicketResponse>(
            $"/api/projects/default/artifacts/{artifactId}/ticket", Json);
        ticket!.Url.Should().StartWith("/artifacts/raw?t=");

        // 2) The raw endpoint is ANONYMOUS (a cross-origin iframe can't send the
        //    bearer); the ticket is the only authority. Render it with a token-less
        //    client to prove that.
        var anon = _factory.CreateClient();
        var rendered = await anon.GetAsync(ticket.Url);

        rendered.StatusCode.Should().Be(HttpStatusCode.OK);
        rendered.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await rendered.Content.ReadAsStringAsync()).Should().Contain("<h1>Demo</h1>");

        // 3) The per-document CSP is the F3 egress brake: inline scripts run
        //    (fidelity) but there's no network/forms, and it self-sandboxes.
        rendered.Headers.TryGetValues("Content-Security-Policy", out var cspValues)
            .Should().BeTrue();
        var csp = cspValues!.Single();
        csp.Should().Contain("default-src 'none'");
        csp.Should().Contain("script-src 'unsafe-inline'");
        csp.Should().Contain("form-action 'none'");
        csp.Should().Contain("sandbox allow-scripts");
        csp.Should().NotContain("connect-src"); // falls back to default-src 'none'
        rendered.Headers.TryGetValues("X-Content-Type-Options", out var nosniff)
            .Should().BeTrue();
        nosniff!.Single().Should().Be("nosniff");
    }

    [Theory]
    [InlineData("/artifacts/raw")] // no ticket
    [InlineData("/artifacts/raw?t=garbage")] // not a ticket
    [InlineData("/artifacts/raw?t=Zm9v.YmFy")] // well-formed b64url, bad signature
    public async Task Raw_endpoint_rejects_missing_or_forged_tickets(string path)
    {
        var anon = _factory.CreateClient();
        var response = await anon.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Ticket_endpoint_requires_auth_and_a_real_artifact()
    {
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/projects/default/artifacts/whatever/ticket"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var client = Authed();
        (await client.GetAsync("/api/projects/default/artifacts/does-not-exist/ticket"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Run the html-fence fixture to completion and return the persisted
    /// artifact id (the canvas list reproduces it once the detached turn ends).</summary>
    private async Task<string> SeedHtmlArtifactAsync(HttpClient client)
    {
        var createResponse = await client.PostAsJsonAsync(
            "/api/projects/default/conversations",
            new CreateConversationRequest { Title = "Render ticket", Tools = ArtifactTools },
            Json);
        var conversation = await createResponse.Content.ReadFromJsonAsync<Conversation>(Json);

        var post = await client.PostAsJsonAsync(
            $"/api/projects/default/conversations/{conversation!.Id}/messages",
            new SendMessageRequest { Content = "make me a demo html page" },
            Json);
        var accepted = await post.Content.ReadFromJsonAsync<TurnAccepted>(Json);

        // Drain the stream so the detached turn finishes and the artifact persists.
        using var stream = await client.GetAsync(
            $"/api/projects/default/conversations/{conversation.Id}/stream?after={accepted!.Seq}",
            HttpCompletionOption.ResponseHeadersRead);
        _ = await ReadUntilTerminalAsync(stream);

        var listed = await client.GetFromJsonAsync<IReadOnlyList<Artifact>>(
            $"/api/projects/default/conversations/{conversation.Id}/artifacts", Json);
        return listed.Should().ContainSingle().Subject.Id;
    }

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
