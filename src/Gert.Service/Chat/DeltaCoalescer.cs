using System.Text;
using Gert.Model.Events;

namespace Gert.Service.Chat;

/// <summary>
/// The coalesce half of the old <c>DeltaSink</c> (refactor: split accumulate from coalesce):
/// it batches text/reasoning deltas into ONE <see cref="DeltaEvent"/>/<see cref="ReasoningEvent"/>
/// each (one seq = one durable row = one publish) on the time/size thresholds and at every
/// boundary, then emits through the caller's append-then-publish sink. This is the live-path
/// optimization that cuts <c>turn_events</c> write/publish amplification an order of magnitude;
/// it has no accumulators (that is <see cref="Model.Agent.DeltaAccumulator"/>) and never reads
/// I/O - the splice stays exact because the streamer dedups by seq, not token granularity.
///
/// <para>
/// Reasoning always precedes content within a round, so its buffer flushes first at every
/// boundary (<see cref="FlushBoundary"/>). The window opens at construction, so after a typical
/// prefill the first token flushes immediately and time-to-first-token stays the model's.
/// </para>
/// </summary>
public sealed class DeltaCoalescer
{
    private readonly Func<ChatEvent, CancellationToken, Task> _emit;
    private readonly TimeSpan _flushInterval;
    private readonly int _flushMaxChars;
    private readonly TimeProvider _clock;

    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _pendingReasoning = new();
    private long _lastFlushTs;
    private long _lastReasoningFlushTs;

    public DeltaCoalescer(
        Func<ChatEvent, CancellationToken, Task> emit,
        TimeSpan flushInterval,
        int flushMaxChars,
        TimeProvider clock)
    {
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _flushInterval = flushInterval;
        _flushMaxChars = flushMaxChars;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _lastFlushTs = clock.GetTimestamp();
        _lastReasoningFlushTs = clock.GetTimestamp();
    }

    /// <summary>Buffer a thinking-text chunk and flush the reasoning buffer if due.</summary>
    public async Task AppendReasoning(string delta, CancellationToken cancellationToken)
    {
        _pendingReasoning.Append(delta);

        var due = _flushInterval <= TimeSpan.Zero
            || _pendingReasoning.Length >= _flushMaxChars
            || _clock.GetElapsedTime(_lastReasoningFlushTs) >= _flushInterval;
        if (due)
        {
            await FlushReasoning(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Buffer an answer-text chunk and flush (reasoning-first) if due.</summary>
    public async Task AppendText(string delta, CancellationToken cancellationToken)
    {
        _pending.Append(delta);

        var due = _flushInterval <= TimeSpan.Zero
            || _pending.Length >= _flushMaxChars
            || _clock.GetElapsedTime(_lastFlushTs) >= _flushInterval;
        if (due)
        {
            // Reasoning-first: any just-appended thinking precedes this content on the wire.
            await FlushBoundary(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Flush the pending thinking buffer as one <see cref="ReasoningEvent"/> (no-op when empty).</summary>
    public async Task FlushReasoning(CancellationToken cancellationToken)
    {
        if (_pendingReasoning.Length == 0)
        {
            return;
        }

        var text = _pendingReasoning.ToString();
        _pendingReasoning.Clear();
        _lastReasoningFlushTs = _clock.GetTimestamp();
        await _emit(new ReasoningEvent { Text = text }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Boundary flush, reasoning before content: all of a round's text precedes its tool events, and
    /// the final round's text precedes citations/message_end.
    /// </summary>
    public async Task FlushBoundary(CancellationToken cancellationToken)
    {
        await FlushReasoning(cancellationToken).ConfigureAwait(false);

        if (_pending.Length == 0)
        {
            return;
        }

        var text = _pending.ToString();
        _pending.Clear();
        _lastFlushTs = _clock.GetTimestamp();
        await _emit(new DeltaEvent { Text = text }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort tail flush: flush whatever is buffered (reasoning first) so the durable log
    /// carries everything that streamed. Skipped on a cancelled token - the emit would throw, and
    /// the caller's cancel finalize emits its own terminal event on a fresh token.
    /// </summary>
    public async Task FlushTails(CancellationToken cancellationToken)
    {
        if ((_pending.Length == 0 && _pendingReasoning.Length == 0) || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await FlushBoundary(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Unwinding an exception already - the row's content is the backstop.
        }
    }
}
