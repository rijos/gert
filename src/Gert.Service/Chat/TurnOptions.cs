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
