namespace Gert.Service.Chat;

/// <summary>
/// The loop's final answer + metrics, for the driver's terminal finalize:
/// accumulated content/reasoning, the last-round token counts, and the pure
/// generation time (stream spans only - tool execution excluded).
/// </summary>
public sealed record AgentLoopResult
{
    /// <summary>The full assistant text streamed across all rounds.</summary>
    public required string Content { get; init; }

    /// <summary>The full thinking text streamed across all rounds.</summary>
    public required string Reasoning { get; init; }

    /// <summary>The last round's completion token count (null if the provider reported none).</summary>
    public int? TokenCount { get; init; }

    /// <summary>The largest round's prompt token count (the turn's real context footprint).</summary>
    public int? PromptTokens { get; init; }

    /// <summary>Pure generation time in stopwatch ticks - stream consumption only, no tool gaps.</summary>
    public long GenElapsedTicks { get; init; }

    /// <summary>Completed tool rounds (the upstream round count is this + the final answer round).</summary>
    public int ToolRounds { get; init; }
}
