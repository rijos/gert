namespace Gert.Tools.Results;

/// <summary>
/// The model-facing payload of the sub-agent tool: only the nested conversation's final
/// <see cref="Result"/> text (the intermediate rounds never enter the parent history) plus the
/// <see cref="Rounds"/> it took. Serialized snake_case to <c>{ "result": ..., "rounds": N }</c>.
/// </summary>
public sealed record SubAgentResult
{
    public required string? Result { get; init; }

    public required int Rounds { get; init; }
}
