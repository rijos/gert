namespace Gert.Service.Tools;

/// <summary>
/// The tool registry (auth.md § tool entitlements): the small, generic catalog
/// of capability ids the system knows about. Granting a tool is listing its id
/// in <c>gert_tools</c>; adding a tool is registering it here — no schema change,
/// no per-tool branch. Pure (no I/O): it only normalizes/intersects ids and
/// looks tools up. This is the one concrete type in <c>Gert.Service</c>'s seam
/// layer; the tool implementations themselves arrive in U7c.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _byId;

    /// <summary>Build a registry over the given tools (keyed by <see cref="ITool.Id"/>).</summary>
    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _byId = tools.ToDictionary(t => t.Id, StringComparer.Ordinal);
        AllIds = _byId.Keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every capability id in the registry — the blanket-grant (<c>*</c>) set.</summary>
    public IReadOnlySet<string> AllIds { get; }

    /// <summary>The registered tools.</summary>
    public IReadOnlyCollection<ITool> All => _byId.Values;

    /// <summary>Look up a tool by capability id, or null if not registered.</summary>
    public ITool? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>True if <paramref name="id"/> is a registered capability id.</summary>
    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>
    /// Parse a space/comma-delimited <c>gert_tools</c> value and intersect it with
    /// the registry — unknown ids are dropped (auth.md § the claim is the ceiling).
    /// </summary>
    public IReadOnlySet<string> Normalize(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new HashSet<string>(StringComparer.Ordinal)
            : Normalize(raw.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// Intersect a set of requested ids with the registry — unknown ids are
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
