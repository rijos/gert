namespace Gert.Service.Tools;

/// <summary>
/// Configuration for the tool-entitlement seam (auth.md § tool entitlements).
/// Bound from the <c>Tools</c> configuration section. The only setting today is
/// the <see cref="DefaultGrant"/> — the set of capability ids a user receives
/// when their token carries no <c>gert_tools</c> claim.
/// </summary>
public sealed class ToolOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Tools";

    /// <summary>
    /// The capability ids granted when the <c>gert_tools</c> claim is absent or
    /// blank (auth.md: default <c>rag search</c>; <c>sandbox</c> is opt-in). These
    /// are the raw configured ids; the resolver still intersects them with the
    /// registry, so an id that names no registered tool is silently dropped.
    /// </summary>
    public IReadOnlyList<string> DefaultGrant { get; set; } = ["rag", "search"];
}
