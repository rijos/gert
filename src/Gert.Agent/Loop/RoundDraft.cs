using Microsoft.Extensions.AI;

namespace Gert.Agent.Loop;

/// <summary>
/// One round's streamed outcome from <c>StreamRoundAsync</c>: the completed tool calls the model
/// emitted (empty = its final answer), the round's token counts, and the pure generation time
/// (stream consumption only - tool execution happens between rounds). The orchestrator folds the
/// counts across rounds (last-round-wins) and decides whether to execute or wind down.
/// </summary>
internal sealed record RoundDraft
{
    public required IReadOnlyList<FunctionCallContent> ToolCalls { get; init; }

    public int? TokenCount { get; init; }

    public int? PromptTokens { get; init; }

    public long GenTicks { get; init; }
}
