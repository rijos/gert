using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Gert.Api.Contracts;
using Gert.Model.Dtos;
using Gert.Model.Json;
using Gert.Model.Projects;
using Gert.Service.Projects;
using Gert.Testing;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// The API security gate: contracts for the
/// endpoints, the IDOR / <c>{pid}</c>-tamper headline, admin <c>{key}</c> traversal
/// (F6), RBAC, the CSP/security headers (F1), the ingestion BackgroundService, and
/// the document filename round-trip. Drives the real pipeline via
/// <see cref="GertApiFactory"/>.
/// </summary>
public sealed class ApiBreadthTests : IClassFixture<GertApiFactory>
{
    private static readonly JsonSerializerOptions Json = GertJsonOptions.Default;

    private readonly GertApiFactory _factory;

    public ApiBreadthTests(GertApiFactory factory) => _factory = factory;

    // The factory (IClassFixture) + its DataRoot are shared across all tests in this
    // class, and the ingestion BackgroundService runs async - so a fixed "dev-user"
    // would let one test's lingering work contend with the next on the SAME project
    // DBs. xUnit news up the class per test method, so this field is unique per test:
    // every default-role client gets its own user folder -> full isolation.
    private readonly string _sub = "user-" + Guid.NewGuid().ToString("N");

    private HttpClient Authed(string role = "user")
    {
        var client = _factory.CreateClient();
        var token = role == "user"
            ? _factory.Tokens.Mint(_sub, groups: ["gert-users"], gertTools: "rag search")
            : _factory.TokenFor(role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient AuthedAs(string sub)
    {
        var client = _factory.CreateClient();
        var token = _factory.Tokens.Mint(sub, groups: ["gert-users"], gertTools: "rag search");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Settings_get_returns_defaults_then_put_round_trips()
    {
        var client = Authed();

        var initial = await client.GetFromJsonAsync<UserSettings>("/api/settings", Json);
        initial.Should().NotBeNull();

        var put = await client.PutAsJsonAsync(
            "/api/settings",
            new UpdateSettingsRequest { ReplyLanguage = "nl" },
            Json);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await put.Content.ReadFromJsonAsync<UserSettings>(Json);
        updated!.ReplyLanguage.Should().Be("nl");
    }

    [Fact]
    public async Task Projects_create_list_get_round_trips_and_default_is_listed()
    {
        var client = Authed();

        var create = await client.PostAsJsonAsync(
            "/api/projects",
            new CreateProjectRequest { Name = "Research" },
            Json);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var meta = await create.Content.ReadFromJsonAsync<ProjectMeta>(Json);
        meta!.Name.Should().Be("Research");

        var list = await client.GetFromJsonAsync<IReadOnlyList<ProjectSummary>>("/api/projects", Json);
        list!.Select(p => p.Id).Should().Contain(meta.Id);

        var get = await client.GetAsync($"/api/projects/{meta.Id}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Non_uuid_pid_is_400_problem_details_not_500()
    {
        var client = Authed();

        var response = await client.GetAsync("/api/projects/not-a-uuid/documents");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"service\":\"gert\"");
    }

    [Fact]
    public async Task Non_uuid_pid_on_conversations_is_400_problem_details_not_500()
    {
        var client = Authed();

        var response = await client.GetAsync("/api/projects/not-a-guid/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"service\":\"gert\"");
    }

    [Fact]
    public async Task N_format_guid_pid_on_conversations_is_400_not_a_storage_500()
    {
        // The storage guards require the canonical "D" GUID shape; a no-dash "N"
        // GUID must be rejected at the route boundary, never reach a path-join.
        var client = Authed();

        var response = await client.GetAsync(
            $"/api/projects/{Guid.NewGuid():N}/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("%2e%2e")]
    [InlineData("..%2f..")]
    public async Task Traversal_pid_is_rejected_never_a_successful_resolve(string badPid)
    {
        var client = Authed();

        var response = await client.GetAsync($"/api/projects/{badPid}/documents");

        // A traversal pid is rejected by the controller guard (400) or collapsed by URL
        // normalization to a non-matching route (404). Either way it never resolves to a
        // successful read - and never reaches a path-join inside the caller's folder.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upload_without_file_part_is_400_not_500()
    {
        var client = Authed();

        using var content = new MultipartFormDataContent();
        var response = await client.PostAsync("/api/projects/default/documents", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task User_b_cannot_read_user_a_document_and_sees_none_of_a_data()
    {
        var a = AuthedAs("idor-user-a");
        var b = AuthedAs("idor-user-b");

        var uploaded = await UploadAsync(a, "default", "a-secret.md", "alpha secret notes");
        uploaded.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var docId = await ReadIdAsync(uploaded);

        // B GETs A's document id under B's own (same-named) path -> 404, never A's row.
        var bGet = await b.GetAsync($"/api/projects/default/documents/{docId}");
        bGet.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // B's list never includes A's filename.
        var bList = await b.GetFromJsonAsync<IReadOnlyList<DocumentResponse>>(
            "/api/projects/default/documents", Json);
        bList!.Select(d => d.Name).Should().NotContain("a-secret.md");
    }

    [Fact]
    public async Task Pid_tamper_resolves_only_under_caller_folder_404_for_anothers_project()
    {
        var a = AuthedAs("tamper-user-a");
        var b = AuthedAs("tamper-user-b");

        var projectResponse = await a.PostAsJsonAsync(
            "/api/projects", new CreateProjectRequest { Name = "A-only" }, Json);
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectMeta>(Json);
        var uploaded = await UploadAsync(a, project!.Id, "a-doc.md", "alpha");
        var docId = await ReadIdAsync(uploaded);

        // B points at A's pid. It is a valid UUID, so it passes the shape guard, but it
        // resolves only inside B's own folder -> an empty project, the doc is 404.
        var bGet = await b.GetAsync($"/api/projects/{project.Id}/documents/{docId}");
        bGet.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var bList = await b.GetFromJsonAsync<IReadOnlyList<DocumentResponse>>(
            $"/api/projects/{project.Id}/documents", Json);
        bList!.Should().BeEmpty("B's copy of that pid is its own empty folder, never A's data");
    }

    [Fact]
    public async Task Non_admin_gets_403_branded_problem_on_admin_users()
    {
        var client = Authed("user");

        var response = await client.GetAsync("/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"service\":\"gert\"");
    }

    [Fact]
    public async Task Anonymous_gets_401_on_settings()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/settings");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Html_response_carries_csp_and_security_headers()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/some/client/route");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        response.Headers.TryGetValues("Content-Security-Policy", out var cspValues).Should().BeTrue();
        var csp = cspValues!.Single();
        csp.Should().Contain("default-src 'self'");
        csp.Should().Contain("script-src 'self'");
        csp.Should().Contain("object-src 'none'");
        csp.Should().Contain("base-uri 'none'");
        csp.Should().Contain("frame-ancestors 'none'");

        // connect-src is the exfiltration brake: 'self' + exactly the Pocket ID origin
        // derived from Auth:Authority (the test host's TestTokens.DefaultIssuer).
        csp.Should().Contain("connect-src 'self' https://id.test.local");

        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
        response.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");
        response.Headers.Contains("Permissions-Policy").Should().BeTrue();
    }

    [Fact]
    public async Task Json_api_response_does_not_carry_the_csp()
    {
        var client = Authed();

        var response = await client.GetAsync("/api/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Content-Security-Policy").Should().BeFalse();
    }

    [Fact]
    public async Task Upload_processes_in_background_then_polls_to_ready_with_chunks()
    {
        var client = Authed();

        var uploaded = await UploadAsync(client, "default", "guide.md", BigMarkdown());
        uploaded.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var docId = await ReadIdAsync(uploaded);

        var ready = await PollUntilTerminalAsync(client, "default", docId);
        ready.Status.Should().Be(Gert.Model.Rag.DocumentStatus.Ready);
        ready.ChunkCount.Should().BeGreaterThan(0, "the embedded chunks are retrievable");
    }

    [Fact]
    public async Task Document_list_returns_decoded_original_filename()
    {
        var client = Authed();

        const string exotic = "ünïcödé résumé (v2) - final.md";
        var uploaded = await UploadAsync(client, "default", exotic, "hello");
        uploaded.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var list = await client.GetFromJsonAsync<IReadOnlyList<DocumentResponse>>(
            "/api/projects/default/documents", Json);

        list!.Select(d => d.Name).Should().Contain(exotic);
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        string pid,
        string filename,
        string content)
    {
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(bytes, "file", filename);
        return await client.PostAsync($"/api/projects/{pid}/documents", form);
    }

    private static async Task<string> ReadIdAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<DocumentResponse> PollUntilTerminalAsync(
        HttpClient client,
        string pid,
        string docId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var response = await client.GetAsync($"/api/projects/{pid}/documents/{docId}");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var document = await response.Content.ReadFromJsonAsync<DocumentResponse>(Json);
                if (document!.Status != Gert.Model.Rag.DocumentStatus.Processing)
                {
                    return document;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Document {docId} never left 'processing'.");
    }

    private static string BigMarkdown()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            sb.AppendLine($"# Section {i}");
            sb.AppendLine("Vector databases compared: qdrant, sqlite-vec, pgvector and more text to chunk.");
        }

        return sb.ToString();
    }
}
