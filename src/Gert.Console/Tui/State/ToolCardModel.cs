using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;

namespace Gert.Console.Tui.State;

/// <summary>
/// One tool card: the <c>tool_call</c>/<c>tool_result</c> aggregate for a
/// single call — the analog of the SPA's tool-card component state
/// (<c>tool-card.js</c>).
/// </summary>
public sealed class ToolCardModel
{
    public required string Id { get; init; }

    /// <summary>Capability id (<c>rag</c>, <c>grep</c>, …).</summary>
    public required string Kind { get; init; }

    public ToolCallStatus Status { get; set; }

    /// <summary>One-line request summary (the query / path / command).</summary>
    public string? Summary { get; set; }

    public long? LatencyMs { get; set; }

    public IReadOnlyList<ToolResultHit>? Hits { get; set; }

    public string? Stdout { get; set; }

    public IReadOnlyList<TodoItem>? Todos { get; set; }
}
