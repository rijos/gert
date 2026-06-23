using Gert.Model.Agent;

namespace Gert.Agent.Loop;

/// <summary>
/// The one mutable per-turn fold the M.E.AI pipeline writes and <see cref="AgentLoop"/> reads back into
/// an <see cref="AgentResult"/> (decisions #13). <see cref="FunctionInvokingChatClient"/> orchestrates
/// the rounds and shapes the upstream history, but it computes none of Gert's turn metrics, so the two
/// pipeline halves accumulate them here: the <see cref="LiveIntentChatClient"/> (in front of the inner
/// client) folds the streamed content/reasoning, the last-round token counts, and the pure generation
/// span (stream consumption only - tool execution happens between the inner calls, outside any stream
/// span); the <see cref="GertFunctionInvokingChatClient"/> override records the executed tool-round
/// count. Built once per run, shared by reference between both halves; never touched off-thread (the
/// loop drives one model stream at a time).
/// </summary>
internal sealed class TurnAccumulators
{
    /// <summary>The content/reasoning fold for <see cref="AgentResult.Content"/>/<see cref="AgentResult.Reasoning"/> - applied as each delta streams.</summary>
    public DeltaAccumulator Deltas { get; } = new();

    /// <summary>Pure generation time in stopwatch ticks - the sum of the per-round stream-consumption spans, no tool gaps.</summary>
    public long GenTicks { get; set; }

    /// <summary>The last round's completion token count (null until the provider reports one).</summary>
    public int? TokenCount { get; set; }

    /// <summary>The last non-null round prompt token count (the turn's real context footprint).</summary>
    public int? PromptTokens { get; set; }

    /// <summary>Executed tool rounds - the highest within-budget iteration + 1 (excludes the refused + wind-down rounds).</summary>
    public int ToolRounds { get; set; }
}
