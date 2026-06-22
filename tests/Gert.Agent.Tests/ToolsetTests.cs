using FluentAssertions;
using Gert.Agent.Loop;
using Gert.Service.Chat;
using Gert.Tools;
using Gert.Tools.Hosting;
using Xunit;

namespace Gert.Agent.Tests;

/// <summary>
/// <see cref="Toolset"/> in isolation (the loop's per-run tool view): effective-bounds computation
/// (intrinsic + partial overrides), the entitlement flag, name resolution, the per-tool call tally,
/// and the wind-down withdrawal. The loop-level behaviour built on these lives in AgentLoopTests.
/// </summary>
public sealed class ToolsetTests
{
    private static readonly IReadOnlySet<string> All =
        new HashSet<string>(["search", "rag"], StringComparer.Ordinal);

    private static IReadOnlySet<string> Offered(params string[] ids) =>
        new HashSet<string>(ids, StringComparer.Ordinal);

    [Fact]
    public void A_partial_override_replaces_only_its_named_fields()
    {
        var tool = new FakeTool("search", "web_search", new ToolBounds
        {
            MaxCallsPerTurn = 5,
            CallTimeout = TimeSpan.FromSeconds(30),
            TokenBudget = 1000,
        });
        var perTool = new Dictionary<string, ToolBoundsOverride>
        {
            // Only MaxCallsPerTurn is overridden; CallTimeout/TokenBudget keep the tool's values.
            ["search"] = new() { MaxCallsPerTurn = 2 },
        };

        var set = new Toolset([tool], Offered("search"), All, perTool);

        var effective = set.Resolve("web_search")!.Effective;
        effective.MaxCallsPerTurn.Should().Be(2);
        effective.CallTimeout.Should().Be(TimeSpan.FromSeconds(30));
        effective.TokenBudget.Should().Be(1000);
    }

    [Fact]
    public void No_override_keeps_the_tools_intrinsic_bounds()
    {
        var bounds = ToolBounds.Default with { MaxCallsPerTurn = 7 };
        var tool = new FakeTool("search", "web_search", bounds);

        var set = new Toolset([tool], Offered("search"), All);

        set.Resolve("web_search")!.Effective.Should().Be(bounds);
    }

    [Fact]
    public void Default_bounds_apply_to_a_tool_that_declares_none()
    {
        // A non-search tool ships no intrinsic override, so it gets the concrete defaults: EVERY tool
        // now carries a per-turn call cap (64 = the round budget), generalizing the old search-only
        // cap (turn-budgets.md section 1). This pins the default constants.
        ToolBounds.Default.MaxCallsPerTurn.Should().Be(64);
        ToolBounds.Default.CallTimeout.Should().Be(TimeSpan.FromSeconds(60));
        ToolBounds.Default.TokenBudget.Should().Be(16384);

        var tool = new FakeTool("rag", "search_documents");
        var set = new Toolset([tool], Offered(), All);

        set.Resolve("search_documents")!.Effective.Should().Be(ToolBounds.Default);
    }

    [Fact]
    public void Entitlement_reflects_the_allowed_set()
    {
        var entitled = new FakeTool("search", "web_search");
        var notEntitled = new FakeTool("rag", "search_documents");
        var allowed = new HashSet<string>(["search"], StringComparer.Ordinal);

        var set = new Toolset([entitled, notEntitled], Offered(), allowed);

        set.Resolve("web_search")!.Entitled.Should().BeTrue();
        set.Resolve("search_documents")!.Entitled.Should().BeFalse();
        set.AllowedToolIds.Should().BeSameAs(allowed);
    }

    [Fact]
    public void Resolve_returns_null_for_an_unknown_name()
    {
        var set = new Toolset([new FakeTool("search", "web_search")], Offered(), All);

        set.Resolve("not_a_tool").Should().BeNull();
    }

    [Fact]
    public void TryConsumeCall_enforces_the_cap_then_refuses()
    {
        var tool = new FakeTool("search", "web_search", ToolBounds.Default with { MaxCallsPerTurn = 2 });
        var set = new Toolset([tool], Offered(), All);
        var entry = set.Resolve("web_search")!;

        set.TryConsumeCall(entry).Should().BeTrue();
        set.TryConsumeCall(entry).Should().BeTrue();
        set.TryConsumeCall(entry).Should().BeFalse();
    }

    [Fact]
    public void A_non_positive_cap_is_unlimited()
    {
        var tool = new FakeTool("search", "web_search", ToolBounds.Default with { MaxCallsPerTurn = 0 });
        var set = new Toolset([tool], Offered(), All);
        var entry = set.Resolve("web_search")!;

        Enumerable.Range(0, 100).Should().OnlyContain(_ => set.TryConsumeCall(entry));
    }

    [Fact]
    public void WindDown_withdraws_the_advertised_tools()
    {
        var tool = new FakeTool("search", "web_search");
        var set = new Toolset([tool], Offered("search"), All);

        set.AdvertisedTools.Should().ContainSingle();
        set.WindDown();
        set.AdvertisedTools.Should().BeEmpty();
    }

    [Fact]
    public void AdjustBounds_runs_last_over_the_effective_bounds()
    {
        // The nested sub-agent forces CallTimeout=Zero after overrides are applied.
        var tool = new FakeTool("search", "web_search", ToolBounds.Default with { CallTimeout = TimeSpan.FromSeconds(30) });
        var perTool = new Dictionary<string, ToolBoundsOverride> { ["search"] = new() { CallTimeout = TimeSpan.FromSeconds(10) } };

        var set = new Toolset([tool], Offered(), All, perTool, adjustBounds: b => b with { CallTimeout = TimeSpan.Zero });

        set.Resolve("web_search")!.Effective.CallTimeout.Should().Be(TimeSpan.Zero);
    }

    private sealed class FakeTool(string id, string name, ToolBounds? bounds = null) : ITool
    {
        public string Id => id;

        public string Name => name;

        public string Description => "x";

        public string ParametersSchema => """{"type":"object"}""";

        public ToolBounds Bounds { get; } = bounds ?? ToolBounds.Default;

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult { Success = true });
    }
}
