namespace Gert.Service.Chat;

/// <summary>
/// Tunables for the detached turn pipeline (bound from <c>Gert:Turn</c>).
/// </summary>
public sealed class TurnOptions
{
    /// <summary>
    /// Hard wall-clock cap on one turn (model rounds + tools). The runner aborts
    /// past it and finalises the assistant row as error. Doubles as the orphan
    /// horizon: a <c>streaming</c> row older than this is REPORTED as error by
    /// readers - the in-memory queue is not durable, so a crashed worker never
    /// finalises its row (chat-and-tools.md section detached turns). Both uses measure
    /// from the plan instant (<c>Gert.Agent.TurnJob.PlannedAt</c> = the placeholder's
    /// <c>CreatedAt</c>): the runner budgets only what REMAINS of this cap after
    /// queue wait, so it can never outlive the readers' horizon.
    /// </summary>
    public TimeSpan MaxTurnDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Number of parallel turn lanes the host worker drains. Turns are sharded
    /// by TurnKey hash: one conversation's turns always ride one lane (strictly
    /// ordered); different conversations may run concurrently. 1 = the old
    /// global serial worker. The per-conversation streaming gate
    /// (ux_messages_streaming) is the correctness control; this is only
    /// throughput (decisions section 11).
    /// </summary>
    public int MaxConcurrentTurns { get; set; } = 4;

    /// <summary>
    /// Runaway brake on tool ROUNDS per turn - NOT a work budget. A round is one
    /// upstream completion request that comes back with tool calls; every round
    /// costs a full completion (one upstream POST), and the todo-driven flow
    /// consumes a round per <c>set_todos</c> update, so this is sized an order of
    /// magnitude above legitimate work (rationale in docs/design/turn-budgets.md);
    /// <see cref="MaxTurnDuration"/> is the real budget. Past the cap the runner
    /// refuses further calls with synthetic error results (visible on the tool
    /// cards) and winds the turn down (see <c>TurnRunner</c>).
    /// </summary>
    public int MaxToolRounds { get; set; } = 64;

    /// <summary>
    /// Per-round completion bound: the <c>max_tokens</c> sent upstream on every
    /// completion request. Acts as BOTH the default (applied when neither the
    /// conversation nor the user's per-model settings ask for anything) and the
    /// ceiling (requested values clamp down to it) - so
    /// <see cref="MaxToolRounds"/> x this bounds a turn's total completion
    /// tokens. Mind thinking models: reasoning tokens count against it, so keep
    /// it generous. <c>0</c> or negative disables the bound (requests pass
    /// through unclamped, unset stays unset).
    /// </summary>
    public int MaxTokensPerRound { get; set; } = 16384;

    /// <summary>
    /// Per-turn cap on <c>web_search</c> calls - searches dominate tool-loop
    /// runaway (each one costs an upstream SearXNG round-trip and floods the
    /// prompt with results), so they get a budget tighter than
    /// <see cref="MaxToolRounds"/>. Past the cap, further searches fail with a
    /// synthetic "budget exhausted" result the model can read - visible on the
    /// tool card; the turn continues. <c>0</c> or negative disables the cap.
    /// </summary>
    public int MaxSearchCallsPerTurn { get; set; } = 5;

    /// <summary>
    /// Generic wall-clock backstop on ONE tool execution. Individual tools keep
    /// their own tighter limits (the sandbox wall clock, the search timeouts);
    /// this catches the ones that hang outside them - a wedged database open, a
    /// stuck embedding call's retry chain. A timed-out call fails with a
    /// visible card error and the turn continues; it never kills the turn.
    /// <see cref="TimeSpan.Zero"/> disables the backstop.
    /// </summary>
    public TimeSpan ToolCallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long one <c>ask_user</c> question waits for the user before the tool
    /// returns its graceful "user did not respond" result. The effective wait
    /// is min(this, remaining turn budget - a small grace slice) so the
    /// graceful path always beats the <see cref="MaxTurnDuration"/> error
    /// finalize; the wait is exempt from <see cref="ToolCallTimeout"/>
    /// (<c>ToolType.Modal</c>) or it could never exceed that backstop
    /// (chat-and-tools.md section Ask the user).
    /// </summary>
    public TimeSpan AskUserTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Delta coalescing window: model chunks are buffered and emitted as ONE
    /// delta event (one seq, one durable row, one publish) once this much time
    /// has passed since the last flush. <see cref="TimeSpan.Zero"/> disables
    /// coalescing - every chunk flushes immediately (the pre-coalescing
    /// behavior). Boundaries (tool rounds, end of stream) always flush.
    /// </summary>
    public TimeSpan DeltaFlushInterval { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Size backstop for the coalescing window: the pending buffer flushes as
    /// soon as it reaches this many chars, even mid-interval, so a fast model
    /// can't grow unbounded delta events.
    /// </summary>
    public int DeltaFlushMaxChars { get; set; } = 512;
}
