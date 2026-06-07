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
    /// readers — the in-memory queue is not durable, so a crashed worker never
    /// finalises its row (chat-and-tools.md § detached turns).
    /// </summary>
    public TimeSpan MaxTurnDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Hard cap on tool ROUNDS per turn. A round is one upstream completion
    /// request that comes back with tool calls — executing them and re-prompting
    /// starts the next round, so every round costs a full vLLM completion (one
    /// upstream POST). The todo-driven flow consumes a round per
    /// <c>set_todos</c> update, so the default leaves room for a long checklist
    /// plus retrieval in one turn; <see cref="MaxTurnDuration"/> is the
    /// wall-clock backstop either way. Past the cap the runner refuses further
    /// calls with synthetic error results and winds the turn down
    /// (see <c>TurnRunner</c>).
    /// </summary>
    public int MaxToolRounds { get; set; } = 16;

    /// <summary>
    /// Delta coalescing window: model chunks are buffered and emitted as ONE
    /// delta event (one seq, one durable row, one publish) once this much time
    /// has passed since the last flush. <see cref="TimeSpan.Zero"/> disables
    /// coalescing — every chunk flushes immediately (the pre-coalescing
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
