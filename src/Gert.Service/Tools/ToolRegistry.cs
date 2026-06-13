namespace Gert.Service.Tools;

/// <summary>
/// The tool registry (auth.md section tool entitlements): the small, generic catalog
/// of capability ids the system knows about. Granting a tool is listing its id
/// in <c>gert_tools</c>; adding a tool is registering it here - no schema change,
/// no per-tool branch. Pure (no I/O): it only normalizes/intersects ids and
/// looks tools up. This is the one concrete type in <c>Gert.Service</c>'s seam
/// layer; the tool implementations are registered separately as scoped services.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool?> _byId;

    /// <summary>Build a registry over the given tools (keyed by <see cref="ITool.Id"/>).</summary>
    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _byId = tools.ToDictionary(t => t.Id, t => (ITool?)t, StringComparer.Ordinal);
        AllIds = _byId.Keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Build an <b>id-only</b> registry (no tool instances). The registry's
    /// authorization role - <see cref="Contains"/> / <see cref="Normalize(string)"/>
    /// / <see cref="AllIds"/>, used by the entitlement resolver and the toggle
    /// validators - needs only the set of capability ids, never the scoped tool
    /// objects. This lets the registry stay a process-wide singleton while the
    /// tools themselves are per-request scoped (resolved as <c>IEnumerable&lt;ITool&gt;</c>
    /// by the orchestrator).
    /// </summary>
    public ToolRegistry(IEnumerable<string> toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        _byId = new Dictionary<string, ITool?>(StringComparer.Ordinal);
        foreach (var id in toolIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                _byId[id] = null;
            }
        }

        AllIds = _byId.Keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every capability id in the registry - the blanket-grant (<c>*</c>) set.</summary>
    public IReadOnlySet<string> AllIds { get; }

    /// <summary>
    /// The registered tool <b>instances</b> - empty for an id-only registry. The
    /// orchestrator resolves tools via <c>IEnumerable&lt;ITool&gt;</c>, so this is
    /// only populated for the instance-backed registry (e.g. tests).
    /// </summary>
    public IReadOnlyCollection<ITool> All => _byId.Values.Where(t => t is not null).Cast<ITool>().ToList();

    /// <summary>
    /// Look up a tool <b>instance</b> by capability id, or null if not registered or
    /// id-only. Use <see cref="Contains"/> for an existence check that works on both
    /// registry shapes.
    /// </summary>
    public ITool? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>True if <paramref name="id"/> is a registered capability id.</summary>
    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>
    /// Parse a space/comma-delimited <c>gert_tools</c> value and intersect it with
    /// the registry - unknown ids are dropped (auth.md section the claim is the ceiling).
    /// </summary>
    public IReadOnlySet<string> Normalize(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new HashSet<string>(StringComparer.Ordinal)
            : Normalize(raw.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// Intersect a set of requested ids with the registry - unknown ids are
    /// dropped, so the result can never name a tool the system doesn't have.
    /// </summary>
    public IReadOnlySet<string> Normalize(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (id is not null && _byId.ContainsKey(id))
            {
                result.Add(id);
            }
        }

        return result;
    }
}
