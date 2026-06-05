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
    /// Coalescing threshold for durable delta rows: accumulated delta text is
    /// flushed to <c>turn_events</c> when it reaches this many chars (and always
    /// at tool/message boundaries). Live subscribers still get per-chunk deltas
    /// via the bus; this only bounds replay-log granularity.
    /// </summary>
    public int DeltaFlushChars { get; set; } = 512;
}
