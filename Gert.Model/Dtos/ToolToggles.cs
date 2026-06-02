using System.Text.Json.Serialization;

namespace Gert.Model.Dtos;

/// <summary>
/// Per-conversation / per-request tool preferences — the <c>tools_json</c> shape
/// (rest-api.md § sending a message; storage-and-data.md § chat.db). A generic,
/// registry-driven map of <c>tool id → enabled</c> (e.g.
/// <c>{"rag":true,"search":true,"sandbox":false}</c>); adding a tool needs no
/// change here. These are *preferences*; the JWT entitlement is the hard ceiling
/// (auth.md § entitlement). Tool ids are validated against the <c>ToolRegistry</c>
/// in the service layer — this type carries no knowledge of which ids exist.
/// </summary>
[JsonConverter(typeof(ToolTogglesJsonConverter))]
public sealed class ToolToggles : IEquatable<ToolToggles>
{
    private readonly IReadOnlyDictionary<string, bool> _toggles;

    /// <summary>An empty toggle set — every tool defaults to off.</summary>
    public ToolToggles()
        : this(new Dictionary<string, bool>(StringComparer.Ordinal))
    {
    }

    /// <summary>Wrap a <c>tool id → enabled</c> map (defensively copied, ordinal keys).</summary>
    public ToolToggles(IReadOnlyDictionary<string, bool> toggles)
    {
        ArgumentNullException.ThrowIfNull(toggles);
        _toggles = new Dictionary<string, bool>(toggles, StringComparer.Ordinal);
    }

    /// <summary>The underlying <c>tool id → enabled</c> map.</summary>
    public IReadOnlyDictionary<string, bool> Toggles => _toggles;

    /// <summary>The ids the user has explicitly switched on (the requested set).</summary>
    public IReadOnlySet<string> EnabledIds =>
        _toggles.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>True if <paramref name="id"/> is present and switched on.</summary>
    public bool IsEnabled(string id) => _toggles.TryGetValue(id, out var on) && on;

    /// <inheritdoc />
    public bool Equals(ToolToggles? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_toggles.Count != other._toggles.Count)
        {
            return false;
        }

        foreach (var (key, value) in _toggles)
        {
            if (!other._toggles.TryGetValue(key, out var otherValue) || otherValue != value)
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ToolToggles);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in _toggles.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            hash.Add(key);
            hash.Add(_toggles[key]);
        }

        return hash.ToHashCode();
    }
}
