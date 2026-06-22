using System.Text.Json;
using Gert.Agent.Loop;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Tools;

namespace Gert.Agent;

/// <summary>
/// The orchestrator's view of a single executed tool call - the persisted/event
/// shapes derived from a tool's <see cref="ToolResult"/>: the recorded kind and
/// status, the latency, the JSON fed back to the model, the citations to collect,
/// and the result hits to render on the tool card.
/// </summary>
internal sealed record ToolOutcome
{
    public required string Kind { get; init; }

    public required ToolCallStatus Status { get; init; }

    public long? LatencyMs { get; init; }

    /// <summary>The JSON fed back to the model as the tool message content.</summary>
    public string? ResponseJson { get; init; }

    /// <summary>Human-readable failure text for the tool card (null on success).</summary>
    public string? Error { get; init; }

    public IReadOnlyList<Citation> Citations { get; init; } = [];

    public IReadOnlyList<ToolResultHit>? Hits { get; init; }

    /// <summary>Plain-text card output (sandbox stdout, the clock reading).</summary>
    public string? Stdout { get; init; }

    /// <summary>The todo list for the todo card (the <c>set_todos</c> tool).</summary>
    public IReadOnlyList<TodoItem>? Todos { get; init; }

    /// <summary>Artifacts the call created/updated, for the canvas (make/edit tools).</summary>
    public IReadOnlyList<Artifact>? Artifacts { get; init; }

    /// <summary>
    /// Build an outcome from a tool execution: the model-facing <paramref name="result"/> (status +
    /// payload) plus the side-effects the tool pushed to its per-call <paramref name="card"/>
    /// (citations/artifacts/stdout/todos). A failed call carries no card side-effects (its payload, if
    /// any, rides <see cref="ResponseJson"/> - e.g. the sandbox's exit_code/stderr).
    /// </summary>
    public static ToolOutcome From(string kind, ToolResult result, ToolCardCollector card, long latencyMs)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (!result.Success)
        {
            var error = result.Error ?? "tool failed";
            var errorJson = result.ResultJson
                            ?? JsonSerializer.Serialize(new { error });
            return new ToolOutcome
            {
                Kind = kind,
                Status = ToolCallStatus.Error,
                LatencyMs = latencyMs,
                ResponseJson = errorJson,
                Error = error,
            };
        }

        return new ToolOutcome
        {
            Kind = kind,
            Status = ToolCallStatus.Done,
            LatencyMs = latencyMs,
            ResponseJson = result.ResultJson,
            Citations = card.Citations,
            Hits = ToolResultHit.FromCitations(card.Citations),
            Stdout = card.Stdout,
            Todos = card.Todos,
            Artifacts = card.Artifacts,
        };
    }

    /// <summary>A failure that never reached the tool (unknown / not permitted / threw).</summary>
    public static ToolOutcome Failure(string kind, string error, long? latencyMs = null) => new()
    {
        Kind = kind,
        Status = ToolCallStatus.Error,
        LatencyMs = latencyMs,
        ResponseJson = JsonSerializer.Serialize(new { error }),
        Error = error,
    };
}
