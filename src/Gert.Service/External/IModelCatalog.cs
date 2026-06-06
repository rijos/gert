using Gert.Model;
using Gert.Model.Dtos;

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

    /// <summary>
    /// Whether <paramref name="modelId"/> accepts image input. Same permissive
    /// stance as <see cref="SupportsTools"/>: only a catalog entry that declares
    /// capabilities without <c>vision</c> gates — the planner then drops images
    /// from the upstream prompt rather than erroring the turn.
    /// </summary>
    bool SupportsVision(string modelId);

    /// <summary>
    /// The model's declared sampling for thinking-OFF turns, or null. Models
    /// whose checkpoint <c>generation_config.json</c> only carries the
    /// thinking-mode set (Qwen3.6) need the instruct set sent explicitly, or
    /// vLLM decodes with the wrong mode's sampling. Applied by the planner as
    /// the last fallback — conversation and user settings always win.
    /// </summary>
    GenerationParams? InstructParams(string modelId);
}

/// <summary>Empty, permissive catalog — the default when no host wires a real one.</summary>
public sealed class NullModelCatalog : IModelCatalog
{
    /// <inheritdoc />
    public IReadOnlyList<ModelInfo> List() => [];

    /// <inheritdoc />
    public bool SupportsTools(string modelId) => true;

    /// <inheritdoc />
    public bool SupportsVision(string modelId) => true;

    /// <inheritdoc />
    public GenerationParams? InstructParams(string modelId) => null;
}
