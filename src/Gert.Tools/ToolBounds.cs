namespace Gert.Tools;

/// <summary>
/// A tool's per-turn budget ceiling (turn-budgets.md section 1) - concrete and never unset, so a
/// tool always HAS bounds. <see cref="ITool.Bounds"/> returns these defaults; a tool with an
/// intrinsic ceiling overrides them (e.g. <c>WebSearchTool</c> tightens <see cref="MaxCallsPerTurn"/>),
/// and operator config (<c>Gert:Tools:&lt;id&gt;:Limits</c>) replaces individual fields. The per-run
/// loop copies the effective value into a mutable tracker and never mutates the shared tool.
/// </summary>
public sealed record ToolBounds
{
    /// <summary>Cap on this tool's executed calls per turn; <c>0</c> or less disables the cap. Default = the round budget.</summary>
    public int MaxCallsPerTurn { get; init; } = 64;

    /// <summary>Per-call wall-clock backstop (was <c>Gert:Turn:ToolCallTimeout</c>); <see cref="System.TimeSpan.Zero"/> disables it. Modal tools are exempt.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Allowance for the tool's nested work (reaches the tool as <c>IToolHost.Limits.TokenBudget</c>).</summary>
    public int TokenBudget { get; init; } = 16384;

    /// <summary>The shared concrete defaults - the value <see cref="ITool.Bounds"/> returns unless a tool overrides it.</summary>
    public static ToolBounds Default { get; } = new();
}
