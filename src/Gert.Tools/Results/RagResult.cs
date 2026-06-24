namespace Gert.Tools.Results;

/// <summary>
/// The model-facing payload of the RAG tool: the fused hits the model reads back
/// (the <see cref="Citation"/> side-channel seeds the footnotes separately).
/// Serialized snake_case to <c>{ "hits": [...] }</c>.
/// </summary>
public sealed record RagResult
{
    public required IReadOnlyList<RagHit> Hits { get; init; }
}
