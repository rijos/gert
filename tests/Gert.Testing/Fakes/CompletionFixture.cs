using System.Text.Json.Serialization;

namespace Gert.Testing.Fakes;

/// <summary>One canned chat completion (testing.md A.3).</summary>
public sealed class CompletionFixture
{
    /// <summary><c>exact</c> | <c>contains</c> - how <see cref="When"/> matches the last user message.</summary>
    [JsonPropertyName("match")]
    public string Match { get; init; } = "exact";

    /// <summary>The trimmed last user message this fixture responds to.</summary>
    [JsonPropertyName("when")]
    public string When { get; init; } = string.Empty;

    /// <summary>Content deltas, streamed one chunk per element.</summary>
    [JsonPropertyName("deltas")]
    public IReadOnlyList<string> Deltas { get; init; } = [];

    /// <summary>Thinking deltas (reasoning_content), streamed BEFORE the content deltas.</summary>
    [JsonPropertyName("reasoning_deltas")]
    public IReadOnlyList<string> ReasoningDeltas { get; init; } = [];

    /// <summary>OpenAI finish reason - <c>stop</c>, or <c>tool_calls</c> when <see cref="ToolCall"/> is set.</summary>
    [JsonPropertyName("finish")]
    public string Finish { get; init; } = "stop";

    /// <summary>Optional token-usage block.</summary>
    [JsonPropertyName("usage")]
    public UsageFixture? Usage { get; init; }

    /// <summary>If set, this fixture emits a tool call first (the tool-loop path).</summary>
    [JsonPropertyName("tool_call")]
    public ToolCallFixture? ToolCall { get; init; }

    /// <summary>Played on the follow-up call once the messages carry the tool result.</summary>
    [JsonPropertyName("after_tool")]
    public AfterToolFixture? AfterTool { get; init; }
}
