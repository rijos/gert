using Gert.Model.Chat;
using Gert.Service.Chat;
using Gert.Tools;

namespace Gert.Agent.Loop;

/// <summary>
/// The loop's per-run view of the offered tools (chat-and-tools.md section the tool loop): the
/// advertised specs sent upstream, an O(1) <see cref="Resolve(string)"/> from a model function name
/// to its <see cref="ToolEntry"/> (tool + entitlement + effective bounds), and a per-id call tally
/// the loop consumes against each tool's <c>MaxCallsPerTurn</c>. Built once by the driver from all
/// tools, the advertised <see cref="ChatToolSpec"/>s, the entitlement snapshot, and the operator's
/// <see cref="ToolsOptions.PerTool"/> overrides; <b>effective bounds are computed here</b> (the tool's
/// intrinsic <see cref="ITool.Bounds"/> with each non-null override applied) and copied into the
/// tracker, so the shared singleton tool is never mutated and the loop never fetches config.
/// </summary>
public sealed class Toolset
{
    private readonly Dictionary<string, ToolEntry> _byName;
    private readonly Dictionary<string, int> _callsUsed = new(StringComparer.Ordinal);
    private IReadOnlyList<ChatToolSpec> _advertisedSpecs;

    /// <param name="tools">All resolvable tools (the loop matches model calls against <see cref="ITool.Name"/>).</param>
    /// <param name="advertisedSpecs">The specs sent upstream this run (entitled+enabled+requested subset).</param>
    /// <param name="allowedToolIds">The plan-time entitlement ceiling - the per-call re-check uses it (auth.md).</param>
    /// <param name="perTool">Operator bound overrides by tool id; null = every tool keeps its intrinsic bounds.</param>
    /// <param name="adjustBounds">An optional last-step transform on each effective bounds (the nested sub-agent forces <c>CallTimeout = Zero</c>).</param>
    public Toolset(
        IEnumerable<ITool> tools,
        IReadOnlyList<ChatToolSpec> advertisedSpecs,
        IReadOnlySet<string> allowedToolIds,
        IReadOnlyDictionary<string, ToolBoundsOverride>? perTool = null,
        Func<ToolBounds, ToolBounds>? adjustBounds = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(advertisedSpecs);
        ArgumentNullException.ThrowIfNull(allowedToolIds);

        _advertisedSpecs = advertisedSpecs;
        AllowedToolIds = allowedToolIds;
        _byName = new Dictionary<string, ToolEntry>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            var effective = Effective(tool.Bounds, perTool, tool.Id);
            if (adjustBounds is not null)
            {
                effective = adjustBounds(effective);
            }

            _byName[tool.Name] = new ToolEntry(tool, allowedToolIds.Contains(tool.Id), effective);
        }
    }

    /// <summary>The specs sent on each completion - withdrawn to <c>[]</c> by <see cref="WindDown"/> on the brake.</summary>
    public IReadOnlyList<ChatToolSpec> AdvertisedSpecs => _advertisedSpecs;

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

    /// <summary>Withdraw the advertised tools (the prefix-cache wind-down brake re-renders the tools region empty).</summary>
    public void WindDown() => _advertisedSpecs = [];

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
