using System.Text;
using Gert.Model.Events;

namespace Gert.Agent.Loop;

/// <summary>
/// The loop's stream sink: the single <see cref="Emit"/> primitive (the driver's event channel),
/// the coalesced delta/reasoning buffers, and the full content/reasoning accumulators.
///
/// <para>
/// Delta coalescing: text chunks buffer in <c>_pending</c> (answer) and <c>_pendingReasoning</c>
/// (thinking) and flush as ONE event each (one seq = one durable row = one publish) on the time/size
/// thresholds and at every boundary. The splice stays exact - the streamer dedups by seq, not token
/// granularity - while turn_events write amplification drops an order of magnitude. The accumulators
/// grow per-chunk independently, so a finalize path never depends on a flush. Reasoning always
/// precedes content within a round, so its buffer flushes first at every boundary
/// (<see cref="FlushBoundary"/>) to preserve wire ordering. The window opens at construction, so after
/// a typical prefill the first token flushes immediately and time-to-first-token stays the model's.
/// </para>
/// </summary>
internal sealed class DeltaSink
{
    private readonly Func<ChatEvent, CancellationToken, Task>? _emit;
    private readonly Action<string>? _onText;
    private readonly Action<string>? _onReasoning;
    private readonly TimeSpan _flushInterval;
    private readonly int _flushMaxChars;
    private readonly TimeProvider _clock;

    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _pendingReasoning = new();
    private long _lastFlushTs;
    private long _lastReasoningFlushTs;

    public DeltaSink(AgentLoopRequest request, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(request);
        _emit = request.Emit;
        _onText = request.OnText;
        _onReasoning = request.OnReasoning;
        _flushInterval = request.DeltaFlushInterval;
        _flushMaxChars = request.DeltaFlushMaxChars;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _lastFlushTs = clock.GetTimestamp();
        _lastReasoningFlushTs = clock.GetTimestamp();
    }

    /// <summary>The full assistant text streamed across all rounds.</summary>
    public string Content => _content.ToString();

    /// <summary>The full thinking text streamed across all rounds.</summary>
    public string Reasoning => _reasoning.ToString();

    /// <summary>The content length so far - the mark a round captures to slice its own narration.</summary>
    public int Length => _content.Length;

    /// <summary>The content appended since <paramref name="mark"/> - this round's narration for the assistant tool-calls message.</summary>
    public string ContentSince(int mark) => _content.ToString(mark, _content.Length - mark);

    /// <summary>
    /// The driver's event channel (the in-loop events AND the seam tools emit through). Null on an
    /// autonomous driver: the loop emits nothing and tools see a null emit.
    /// </summary>
    public Task Emit(ChatEvent chatEvent, CancellationToken cancellationToken) =>
        _emit is { } emit ? emit(chatEvent, cancellationToken) : Task.CompletedTask;

    /// <summary>Accumulate a thinking-text chunk, tap the per-chunk sink, and flush the reasoning buffer if due.</summary>
    public async Task AppendReasoning(string delta, CancellationToken cancellationToken)
    {
        _reasoning.Append(delta);
        _pendingReasoning.Append(delta);
        _onReasoning?.Invoke(delta);

        var due = _flushInterval <= TimeSpan.Zero
            || _pendingReasoning.Length >= _flushMaxChars
            || _clock.GetElapsedTime(_lastReasoningFlushTs) >= _flushInterval;
        if (due)
        {
            await FlushReasoning(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Accumulate an answer-text chunk, tap the per-chunk sink, and flush (reasoning-first) if due.</summary>
    public async Task AppendText(string delta, CancellationToken cancellationToken)
    {
        _content.Append(delta);
        _pending.Append(delta);
        _onText?.Invoke(delta);

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
        await Emit(new ReasoningEvent { Text = text }, cancellationToken).ConfigureAwait(false);
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
        await Emit(new DeltaEvent { Text = text }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort tail flush for the loop's <c>finally</c>: a fault mid-stream unwinds with text
    /// still buffered, and a REPLAYING client reads turn_events - flush the tails (reasoning first) so
    /// the durable log carries everything that streamed. Skipped on a cancelled token: the emit would
    /// throw, and the driver's cancel finalize emits its own terminal event on a fresh token.
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
