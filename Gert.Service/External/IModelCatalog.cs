using Gert.Model;

namespace Gert.Service.External;

/// <summary>
/// The configured model catalog (rest-api.md § models) — a port like the other
/// outside-world seams: the real implementation reads operator config in
/// <c>Gert.External</c>; hosts without one fall back to
/// <see cref="NullModelCatalog"/> (empty, permissive).
/// </summary>
public interface IModelCatalog
{
    /// <summary>The catalog entries, in configured order.</summary>
    IReadOnlyList<ModelInfo> List();

    /// <summary>
    /// Whether <paramref name="modelId"/> may be offered tools. Unknown ids and
    /// entries without declared capabilities are PERMISSIVE (true) — only a
    /// catalog entry that declares capabilities without <c>tools</c> gates.
    /// </summary>
    bool SupportsTools(string modelId);
}

/// <summary>Empty, permissive catalog — the default when no host wires a real one.</summary>
public sealed class NullModelCatalog : IModelCatalog
{
    /// <inheritdoc />
    public IReadOnlyList<ModelInfo> List() => [];

    /// <inheritdoc />
    public bool SupportsTools(string modelId) => true;
}
