using Gert.Service.Chat;
using Gert.Tools;
using Gert.Tools.Hosting;
using Microsoft.Extensions.AI;

namespace Gert.Agent.Loop;

/// <summary>
/// Everything <see cref="IAgentLoop.RunAsync"/> needs for one run: the fully-built
/// initial messages (system prompt already prepended by the caller), the per-run
/// <see cref="Toolset"/> (the offered tools + advertised specs + entitlement +
/// effective bounds, built by the driver), the resolved model client, the host, and the
/// turn-budget knobs (the loop reads no <c>IOptions</c>). Output rides the
/// <see cref="IAgentEventSink"/> the loop is run with, not this record. The loop copies
/// <see cref="Messages"/> into its own working list before appending tool rounds, so the
/// caller's list is never mutated.
/// </summary>
public sealed record AgentLoopRequest
{
    /// <summary>
    /// The initial upstream conversation - system message already prepended, then
    /// history. The loop copies this into its own working list and appends the
    /// per-round tool-call/tool-result pairs onto the copy.
    /// </summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// The per-run tool view the loop resolves model calls against: advertised specs,
    /// O(1) name resolution, the entitlement ceiling, and per-tool effective bounds +
    /// call trackers. Built once by the driver (see <see cref="Toolset"/>).
    /// </summary>
    public required Toolset Tools { get; init; }

    /// <summary>The selected provider id sent on each completion request.</summary>
    public required string ModelId { get; init; }

    /// <summary>The resolved chat client (the driver resolves the provider; the loop never does).</summary>
    public required IChatClient Model { get; init; }

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

    /// <summary>Runaway brake on tool rounds (<see cref="TurnOptions.MaxToolRounds"/>).</summary>
    public required int MaxRounds { get; init; }

    /// <summary>Per-round completion cap (<see cref="TurnOptions.MaxTokensPerRound"/>); null leaves it to the provider.</summary>
    public int? MaxTokensPerRound { get; init; }
}
