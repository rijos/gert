using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Gert.Api.Contracts;
using Gert.Api.Controllers;
using Gert.Model.Json;
using Gert.Service;
using Gert.Testing;
using Gert.Testing.Fakes;
using Gert.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// <c>GET /api/tools</c> - the entitled-tool catalog that drives the composer's
/// tools popup (rest-api.md section tools). The list is the registered tools
/// filtered by the caller's <c>gert_tools</c> claim - the SAME hard ceiling the
/// turn planner applies (auth.md section the claim is the ceiling), so the popup
/// can never offer a tool the model would be denied. The popup renders PURELY from
/// each row's display descriptor (title/icon/group/source/requires_human), so these
/// tests pin those fields too - including that an unrenderable icon degrades to the
/// curated fallback (a future MCP tool can only ever ship a glyph the client has).
/// </summary>
public sealed class ToolsApiTests : IClassFixture<GertApiFactory>
{
    // The canonical built-in tool ids (Gert.Tools.Builtin BuiltInToolIds) - a blanket
    // grant (gert_tools = "*") entitles exactly this set.
    private static readonly string[] AllBuiltInToolIds =
        ["rag", "search", "sandbox", "todo", "clock", "make_artifact", "edit_artifact", "read_artifact", "list_artifacts", "ask_user", "fetch", "sub_agent"];

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
        // Every row carries the full display descriptor - none degrade to empty.
        tools.Should().OnlyContain(t =>
            !string.IsNullOrWhiteSpace(t.Id)
            && !string.IsNullOrWhiteSpace(t.Name)
            && !string.IsNullOrWhiteSpace(t.Description)
            && !string.IsNullOrWhiteSpace(t.ToolType)
            && !string.IsNullOrWhiteSpace(t.Title)
            && !string.IsNullOrWhiteSpace(t.Icon)
            && !string.IsNullOrWhiteSpace(t.Group));
    }

    [Fact]
    public async Task Every_row_carries_the_display_descriptor()
    {
        var client = Authed("*");

        var tools = await client.GetFromJsonAsync<IReadOnlyList<WireToolInfo>>("/api/tools", Json);

        tools.Should().NotBeNull();
        // source is "builtin" for every row (the only catalog source today; the menu sections on it).
        tools!.Should().OnlyContain(t => t.Source == "builtin");
        // Every emitted icon is a member of the curated client vocabulary - so the SPA can always
        // render it (an unknown key would have degraded to the fallback before reaching the wire).
        tools.Should().OnlyContain(t => ToolIcons.Keys.Contains(t.Icon));
        // requires_human is true only for ask_user (the one human-in-the-loop tool); all else false.
        tools.Single(t => t.Id == "ask_user").RequiresHuman.Should().BeTrue();
        tools.Where(t => t.Id != "ask_user").Should().OnlyContain(t => !t.RequiresHuman);
        // The grouping the menu sections on: rag -> docs, the artifact suite -> canvas, rest standard.
        tools.Single(t => t.Id == "rag").Group.Should().Be("docs");
        tools.Where(t => t.Id is "make_artifact" or "edit_artifact" or "read_artifact" or "list_artifacts")
            .Should().OnlyContain(t => t.Group == "canvas");
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
            // The display descriptor rides the wire in snake_case alongside the core fields.
            row.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
            row.GetProperty("icon").GetString().Should().NotBeNullOrWhiteSpace();
            row.GetProperty("group").GetString().Should().NotBeNullOrWhiteSpace();
            row.GetProperty("source").GetString().Should().Be("builtin");
            row.TryGetProperty("requires_human", out var rh).Should().BeTrue();
            rh.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        }

        // ask_user is the modal tool; the rest are standard - the popup groups on this.
        var askUser = rows.Single(r => r.GetProperty("id").GetString() == "ask_user");
        askUser.GetProperty("tool_type").GetString().Should().Be("modal");
        askUser.GetProperty("requires_human").GetBoolean().Should().BeTrue();
        var rag = rows.Single(r => r.GetProperty("id").GetString() == "rag");
        rag.GetProperty("tool_type").GetString().Should().Be("standard");
    }

    [Fact]
    public void An_unknown_icon_key_degrades_to_the_curated_fallback()
    {
        // A future MCP tool could declare an icon the client doesn't ship; the catalog must never
        // emit a key the SPA can't render, so the controller degrades it to ToolIcons.Fallback.
        // A good icon passes through unchanged.
        var controller = new ToolsController(
            new ITool[] { new StubTool("bogus_glyph_xyz", "bogus"), new StubTool("globe", "good") },
            new FakeUserContext());

        var rows = ((OkObjectResult)controller.List().Result!).Value
            .Should().BeAssignableTo<IReadOnlyList<ToolInfo>>().Subject;

        rows.Single(r => r.Id == "bogus").Icon.Should().Be(ToolIcons.Fallback);
        rows.Single(r => r.Id == "good").Icon.Should().Be("globe");
        // The fallback is itself a real, renderable key (so the degraded glyph always shows).
        ToolIcons.Keys.Should().Contain(ToolIcons.Fallback);
    }

    [Fact]
    public void Every_built_in_tool_icon_is_a_member_of_the_curated_set()
    {
        // A built-in tool can never ship an unrenderable icon: each tool's Icon must name a key the
        // client has (ToolIcons.Keys, mirroring icons.ts). Resolves the live registered tools.
        using var scope = _factory.Services.CreateScope();

        var icons = scope.ServiceProvider.GetServices<ITool>().Select(t => t.Icon).Distinct().ToList();

        icons.Should().NotBeEmpty();
        icons.Should().OnlyContain(icon => ToolIcons.Keys.Contains(icon));
    }

    [Fact]
    public void Curated_icon_set_matches_the_keys_exported_in_icons_ts()
    {
        // Drift guard: ToolIcons.Keys is the SERVER's mirror of the SPA's icons.ts `GLYPHS` map
        // (icons.ts is the source of truth). Parse the glyph keys straight out of the file and
        // assert set-equality, so adding/removing a client glyph without updating ToolIcons reddens.
        var icons = File.ReadAllText(IconsTsPath());

        // Each glyph is declared as `  <key>: () => [` inside the GLYPHS object (2-space indent).
        var keys = System.Text.RegularExpressions.Regex
            .Matches(icons, @"(?m)^  ([a-z]+): \(\) =>")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        keys.Should().NotBeEmpty();
        ToolIcons.Keys.Should().BeEquivalentTo(keys, "ToolIcons mirrors the icons.ts vocabulary 1:1");
    }

    [Fact]
    public async Task Requires_authentication()
    {
        var response = await _factory.CreateClient().GetAsync("/api/tools");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Walk up from the test binary to the repo root and resolve the SPA's icons.ts (the icon
    // vocabulary's source of truth). Mirrors Gert.Testing.SharedPaths' build-layout-independent walk.
    private static string IconsTsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Gert.Api", "wwwroot", "icons", "icons.ts");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate src/Gert.Api/wwwroot/icons/icons.ts walking up from " + AppContext.BaseDirectory);
    }

    // A minimal ITool for the projection test: only the descriptor fields the controller reads.
    private sealed class StubTool : ITool
    {
        private readonly string _icon;
        private readonly string _id;

        public StubTool(string icon, string id)
        {
            _icon = icon;
            _id = id;
        }

        public string Id => _id;

        public string Name => _id + "_fn";

        public string Description => "stub";

        public string ParametersSchema => "{}";

        public string Icon => _icon;

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, IToolHost host, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    // Mirrors the wire JSON (snake_case, string tool_type) so the test reads the
    // payload exactly as the SPA's WireToolInfo does.
    private sealed record WireToolInfo
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public required string Description { get; init; }

        public required string ToolType { get; init; }

        public required string Title { get; init; }

        public required string Icon { get; init; }

        public required string Group { get; init; }

        public required string Source { get; init; }

        public required bool RequiresHuman { get; init; }
    }
}
