using System.Text;

namespace Gert.Model.Agent;

/// <summary>
/// The pure fold half of the old <c>DeltaSink</c> (refactor: split accumulate from
/// coalesce): <see cref="AgentEvent"/> -> renderable state, no transport, timing, or I/O.
/// Used at two call sites - it builds <see cref="AgentResult"/>'s content as the run goes,
/// and it folds a log slice back to rebuild an in-flight message on reconnect (same fold,
/// no separate "rebuild" path). The accumulators grow per delta independently of any
/// flush, so a finalize never depends on coalescing having happened.
/// </summary>
public sealed class DeltaAccumulator
{
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();

    /// <summary>Apply one event - text/reasoning deltas accumulate; discrete events pass through untouched.</summary>
    public void Apply(AgentEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        switch (ev)
        {
            case TextDelta t:
                _content.Append(t.Text);
                break;
            case ReasoningDelta r:
                _reasoning.Append(r.Text);
                break;
        }
    }

    /// <summary>The full assistant text accumulated so far.</summary>
    public string Content => _content.ToString();

    /// <summary>The full thinking text accumulated so far.</summary>
    public string Reasoning => _reasoning.ToString();

    /// <summary>The content length so far - the mark a round captures to slice its own narration.</summary>
    public int Length => _content.Length;

    /// <summary>The content appended since <paramref name="mark"/> - a round's narration for the assistant tool-calls message.</summary>
    public string ContentSince(int mark) => _content.ToString(mark, _content.Length - mark);
}
