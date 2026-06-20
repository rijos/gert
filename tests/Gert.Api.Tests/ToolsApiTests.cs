using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Gert.Model.Json;
using Gert.Testing;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// <c>GET /api/tools</c> - the entitled-tool catalog that drives the composer's
/// tools popup (rest-api.md section tools). The list is the registered tools
/// filtered by the caller's <c>gert_tools</c> claim - the SAME hard ceiling the
/// turn planner applies (auth.md section the claim is the ceiling), so the popup
/// can never offer a tool the model would be denied.
/// </summary>
public sealed class ToolsApiTests : IClassFixture<GertApiFactory>
{
    // The canonical built-in tool ids (Gert.Tools.Builtin BuiltInToolIds) - a blanket
    // grant (gert_tools = "*") entitles exactly this set.
    private static readonly string[] AllBuiltInToolIds =
        ["rag", "search", "sandbox", "todo", "clock", "make_artifact", "edit_artifact", "read_artifact", "ask_user", "fetch", "sub_agent"];

    private static readonly JsonSerializerOptions Json = GertJsonOptions.Default;

    private readonly GertApiFactory _factory;

    public ToolsApiTests(GertApiFactory factory) => _factory = factory;

    private HttpClient Authed(string gertTools)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _factory.Tokens.Mint("tools-user-" + Guid.NewGuid().ToString("N"), groups: ["gert-users"], gertTools: gertTools));
        return client;
    }

    [Fact]
    public async Task Full_entitlement_lists_every_built_in_tool()
    {
        // "*" is the blanket grant: HttpUserContext resolves it to the whole registry.
        var client = Authed("*");

        var tools = await client.GetFromJsonAsync<IReadOnlyList<WireToolInfo>>("/api/tools", Json);

        tools.Should().NotBeNull();
        tools!.Select(t => t.Id).Should().BeEquivalentTo(AllBuiltInToolIds);
        // Every row carries the four wire fields - none degrade to empty.
        tools.Should().OnlyContain(t =>
            !string.IsNullOrWhiteSpace(t.Id)
            && !string.IsNullOrWhiteSpace(t.Name)
            && !string.IsNullOrWhiteSpace(t.Description)
            && !string.IsNullOrWhiteSpace(t.ToolType));
    }

    [Fact]
    public async Task Limited_entitlement_lists_only_the_granted_subset()
    {
        // The hard ceiling: a claim naming only rag + clock drops everything else,
        // exactly as TurnPlanner.ResolveOfferedTools would.
        var client = Authed("rag clock");

        var tools = await client.GetFromJsonAsync<IReadOnlyList<WireToolInfo>>("/api/tools", Json);

        tools.Should().NotBeNull();
        tools!.Select(t => t.Id).Should().BeEquivalentTo("clock", "rag");
    }

    [Fact]
    public async Task Absent_gert_tools_claim_yields_an_empty_catalog()
    {
        // Fail-closed floor: no gert_tools claim grants ZERO tools (auth.md section 10).
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _factory.Tokens.Mint("untooled-" + Guid.NewGuid().ToString("N"), groups: ["gert-users"], gertTools: null));

        var tools = await client.GetFromJsonAsync<IReadOnlyList<WireToolInfo>>("/api/tools", Json);

        tools.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task Tool_type_serializes_as_the_snake_case_string_enum()
    {
        var client = Authed("*");

        // Read the raw JSON to prove the wire contract: snake_case fields + string
        // enums ("standard" / "modal"), the shape the SPA's WireToolInfo expects.
        using var stream = await client.GetStreamAsync("/api/tools");
        using var doc = await JsonDocument.ParseAsync(stream);

        var rows = doc.RootElement.EnumerateArray().ToList();
        rows.Should().NotBeEmpty();
        foreach (var row in rows)
        {
            row.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
            row.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
            row.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
            row.GetProperty("tool_type").GetString().Should().BeOneOf("standard", "modal");
        }

        // ask_user is the modal tool; the rest are standard - the popup groups on this.
        var askUser = rows.Single(r => r.GetProperty("id").GetString() == "ask_user");
        askUser.GetProperty("tool_type").GetString().Should().Be("modal");
        var rag = rows.Single(r => r.GetProperty("id").GetString() == "rag");
        rag.GetProperty("tool_type").GetString().Should().Be("standard");
    }

    [Fact]
    public async Task Requires_authentication()
    {
        var response = await _factory.CreateClient().GetAsync("/api/tools");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Mirrors the wire JSON (snake_case, string tool_type) so the test reads the
    // payload exactly as the SPA's WireToolInfo does.
    private sealed record WireToolInfo
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public required string Description { get; init; }

        public required string ToolType { get; init; }
    }
}
