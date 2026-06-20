using Gert.Chat;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Service.Chat;
using Gert.Tools;
using Gert.Tools.Hosting;

namespace Gert.Agent.Loop;

/// <summary>
/// Everything <see cref="IAgentLoop.RunAsync"/> needs for one run: the fully-built
/// initial messages (system prompt already prepended by the caller), the offered
/// tools + their specs, the resolved model client, the host, the entitlement
/// ceiling, the turn-budget knobs (the loop reads no <c>IOptions</c>), and the
/// driver's callbacks. The loop copies <see cref="Messages"/> into its own working
/// list before appending tool rounds, so the caller's list is never mutated.
/// </summary>
public sealed record AgentLoopRequest
{
    /// <summary>
    /// The initial upstream conversation - system message already prepended, then
    /// history. The loop copies this into its own working list and appends the
    /// per-round tool-call/tool-result pairs onto the copy.
    /// </summary>
    public required IReadOnlyList<ChatModelMessage> Messages { get; init; }

    /// <summary>Model-facing specs for the offered tools.</summary>
    public IReadOnlyList<ChatToolSpec> ToolSpecs { get; init; } = [];

    /// <summary>The tool instances the loop resolves model calls against (by <see cref="ITool.Name"/>).</summary>
    public IReadOnlyList<ITool> Tools { get; init; } = [];

    /// <summary>The selected provider id sent on each completion request.</summary>
    public required string ModelId { get; init; }

    /// <summary>The resolved chat client (the driver resolves the provider; the loop never does).</summary>
    public required IChatModelClient Model { get; init; }

    /// <summary>The capability surface handed to each tool - built once for the run by the driver.</summary>
    public required IToolHost Host { get; init; }

    /// <summary>The active project - rides each <see cref="ToolInvocation"/> (RAG scoping).</summary>
    public required string Pid { get; init; }

    /// <summary>The conversation each tool call runs in - rides each invocation (artifact scoping).</summary>
    public string? ConversationId { get; init; }

    /// <summary>The assistant message producing the calls - rides each invocation (artifact provenance).</summary>
    public string? MessageId { get; init; }

    /// <summary>The caller's IANA timezone - rides each invocation (the clock tool's default zone).</summary>
    public string? ClientTimezone { get; init; }

    /// <summary>The entitlement ceiling - the per-call re-check refuses anything outside it (auth.md).</summary>
    public required IReadOnlySet<string> AllowedToolIds { get; init; }

    /// <summary>Runaway brake on tool rounds (<see cref="TurnOptions.MaxToolRounds"/>).</summary>
    public required int MaxRounds { get; init; }

    /// <summary>Per-round completion cap (<see cref="TurnOptions.MaxTokensPerRound"/>); null leaves it to the provider.</summary>
    public int? MaxTokensPerRound { get; init; }

    /// <summary>Per-turn <c>web_search</c> budget (<see cref="TurnOptions.MaxSearchCallsPerTurn"/>); &lt;= 0 disables it.</summary>
    public required int MaxSearchCallsPerTurn { get; init; }

    /// <summary>Generic per-call timeout backstop (<see cref="TurnOptions.ToolCallTimeout"/>); Modal tools are exempt.</summary>
    public required TimeSpan ToolCallTimeout { get; init; }

    /// <summary>Delta coalescing window (<see cref="TurnOptions.DeltaFlushInterval"/>).</summary>
    public required TimeSpan DeltaFlushInterval { get; init; }

    /// <summary>Size backstop for the coalescing window (<see cref="TurnOptions.DeltaFlushMaxChars"/>).</summary>
    public required int DeltaFlushMaxChars { get; init; }

    /// <summary>
    /// The driver's event sink for the loop's in-loop events (delta, reasoning,
    /// tool_call running, tool_result, artifact) AND the seam handed to tools as
    /// <see cref="ToolInvocation.EmitAsync"/>. Null on an autonomous driver: the
    /// loop emits nothing and tools see a null emit (ask_user fails closed).
    /// </summary>
    public Func<ChatEvent, CancellationToken, Task>? Emit { get; init; }

    /// <summary>
    /// Called once per ENTITLED executed tool call, with everything the driver
    /// needs to persist the tool_call row + collect that call's citations bound
    /// to the row id. Null = don't persist (autonomous drivers).
    /// </summary>
    public Func<ExecutedToolCall, CancellationToken, Task>? OnToolExecuted { get; init; }

    /// <summary>
    /// Called at each tool boundary with the accumulated content so the driver can
    /// flush the streaming row (so thread reads see progress). Null = no progress sink.
    /// </summary>
    public Func<string, CancellationToken, Task>? OnProgress { get; init; }

    /// <summary>
    /// Per-CHUNK answer-text sink (NOT coalesced), so a driver's own buffer stays
    /// live even when the loop throws mid-stream - its error/cancel finalize then
    /// carries the partial content the tail-flush never emitted (a cancelled token
    /// skips the tail). The coalesced DELTA event still rides <see cref="Emit"/>.
    /// Null = the driver reads only the returned result.
    /// </summary>
    public Action<string>? OnText { get; init; }

    /// <summary>Per-chunk thinking-text sink, the reasoning twin of <see cref="OnText"/>.</summary>
    public Action<string>? OnReasoning { get; init; }
}
