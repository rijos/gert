namespace Gert.Service.Chat;

/// <summary>
/// A <b>partial</b> override of a tool's <c>ToolBounds</c> (bound from
/// <c>Gert:Tools:&lt;toolId&gt;:Limits</c>): only the non-null fields replace the tool's defaults, so
/// an absent field never clobbers a concrete intrinsic value. The nullable shape is deliberate - the
/// defaults live on the tool, not here (turn-budgets.md section 1).
/// </summary>
public sealed class ToolBoundsOverride
{
    /// <summary>Override for the per-turn executed-call cap; null keeps the tool's default.</summary>
    public int? MaxCallsPerTurn { get; set; }

    /// <summary>Override for the per-call wall-clock backstop; null keeps the tool's default.</summary>
    public TimeSpan? CallTimeout { get; set; }

    /// <summary>Override for the nested-work token allowance; null keeps the tool's default.</summary>
    public int? TokenBudget { get; set; }
}
