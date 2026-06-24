namespace Gert.Service.Chat;

/// <summary>
/// Per-tool budget overrides (bound from <c>Gert:Tools:&lt;toolId&gt;:Limits</c>). Each tool ships
/// concrete intrinsic bounds (<c>ToolBounds</c> on the <c>ITool</c>); the host fills <see cref="PerTool"/>
/// per registered tool id from any <c>Limits</c> section present, and the per-run loop applies the
/// override field-by-field over the tool's defaults (turn-budgets.md section 1). Keyed by tool id
/// case-insensitively, matching the entitlement/registry id space.
/// </summary>
public sealed class ToolsOptions
{
    /// <summary>Overrides by tool id; absent id = the tool's intrinsic bounds apply unchanged.</summary>
    public Dictionary<string, ToolBoundsOverride> PerTool { get; } = new(StringComparer.OrdinalIgnoreCase);
}
