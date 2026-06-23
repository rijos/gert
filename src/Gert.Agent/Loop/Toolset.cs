using Gert.Service.Chat;
using Gert.Tools;
using Microsoft.Extensions.AI;

namespace Gert.Agent.Loop;

/// <summary>
/// The loop's per-run view of the offered tools (chat-and-tools.md section the tool loop): the
/// advertised <see cref="AITool"/>s sent upstream on <see cref="ChatOptions.Tools"/>, an O(1)
/// <see cref="Resolve(string)"/> from a model function name to its <see cref="ToolEntry"/> (tool +
/// entitlement + effective bounds), and a per-id call tally the loop consumes against each tool's
/// <c>MaxCallsPerTurn</c>. Built once by the driver from all resolvable tools, the offered subset's
/// ids, the entitlement snapshot, and the operator's <see cref="ToolsOptions.PerTool"/> overrides;
/// <b>effective bounds are computed here</b> (the tool's intrinsic <see cref="ITool.Bounds"/> with each
/// non-null override applied) and copied into the tracker, so the shared singleton tool is never
/// mutated and the loop never fetches config. The advertised tools are lean
/// <see cref="ToolFunction"/>s (the tool's own compact schema), not M.E.AI-synthesised declarations -
/// the tools region is a token budget.
/// </summary>
public sealed class Toolset
{
    private readonly Dictionary<string, ToolEntry> _byName;
    private readonly Dictionary<string, int> _callsUsed = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<AITool> _advertisedTools;

    /// <param name="tools">All resolvable tools (the loop matches model calls against <see cref="ITool.Name"/>).</param>
    /// <param name="offeredToolIds">The ids advertised upstream this run (entitled+enabled+requested subset).</param>
    /// <param name="allowedToolIds">The plan-time entitlement ceiling - the per-call re-check uses it (auth.md).</param>
    /// <param name="perTool">Operator bound overrides by tool id; null = every tool keeps its intrinsic bounds.</param>
    /// <param name="adjustBounds">An optional last-step transform on each effective bounds (the nested sub-agent forces <c>CallTimeout = Zero</c>).</param>
    public Toolset(
        IEnumerable<ITool> tools,
        IReadOnlySet<string> offeredToolIds,
        IReadOnlySet<string> allowedToolIds,
        IReadOnlyDictionary<string, ToolBoundsOverride>? perTool = null,
        Func<ToolBounds, ToolBounds>? adjustBounds = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(offeredToolIds);
        ArgumentNullException.ThrowIfNull(allowedToolIds);

        AllowedToolIds = allowedToolIds;
        _byName = new Dictionary<string, ToolEntry>(StringComparer.Ordinal);

        // Advertise in the tools' own order (registry order from the driver), filtered to the offered
        // subset; resolve over ALL tools so a non-offered call still maps to its entry for the
        // entitlement re-check (it is then refused, never silently run).
        var advertised = new List<AITool>();
        foreach (var tool in tools)
        {
            var effective = Effective(tool.Bounds, perTool, tool.Id);
            if (adjustBounds is not null)
            {
                effective = adjustBounds(effective);
            }

            _byName[tool.Name] = new ToolEntry(tool, allowedToolIds.Contains(tool.Id), effective);
            if (offeredToolIds.Contains(tool.Id))
            {
                advertised.Add(new ToolFunction(tool));
            }
        }

        _advertisedTools = advertised;
    }

    /// <summary>
    /// The tools advertised on each completion. The wind-down to <c>[]</c> on the final round is
    /// handled by M.E.AI's <c>FunctionInvokingChatClient.MaximumIterationsPerRequest</c> (it clears
    /// the advertised tools on its tools-cleared iteration), not by this type.
    /// </summary>
    public IReadOnlyList<AITool> AdvertisedTools => _advertisedTools;

    /// <summary>The plan-time entitlement snapshot - rides each <see cref="ToolInvocation"/> as the nested ceiling.</summary>
    public IReadOnlySet<string> AllowedToolIds { get; }

    /// <summary>Resolve a model function name to its entry, or null when it names no offered tool.</summary>
    public ToolEntry? Resolve(string functionName) => _byName.GetValueOrDefault(functionName);

    /// <summary>
    /// Consume one unit of <paramref name="entry"/>'s per-turn call budget. A cap of <c>0</c> or less
    /// is unlimited (always passes); a call past the cap returns false and the caller refuses it with
    /// a synthetic result - the wire format needs a result per call.
    /// </summary>
    public bool TryConsumeCall(ToolEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var cap = entry.Effective.MaxCallsPerTurn;
        if (cap <= 0)
        {
            return true;
        }

        var used = _callsUsed.GetValueOrDefault(entry.Tool.Id);
        if (used >= cap)
        {
            return false;
        }

        _callsUsed[entry.Tool.Id] = used + 1;
        return true;
    }

    /// <summary>Effective bounds: the tool's intrinsic <see cref="ITool.Bounds"/> with each non-null override field applied.</summary>
    private static ToolBounds Effective(
        ToolBounds baseline,
        IReadOnlyDictionary<string, ToolBoundsOverride>? perTool,
        string toolId)
    {
        if (perTool is null || !perTool.TryGetValue(toolId, out var ov) || ov is null)
        {
            return baseline;
        }

        return baseline with
        {
            MaxCallsPerTurn = ov.MaxCallsPerTurn ?? baseline.MaxCallsPerTurn,
            CallTimeout = ov.CallTimeout ?? baseline.CallTimeout,
            TokenBudget = ov.TokenBudget ?? baseline.TokenBudget,
        };
    }
}
