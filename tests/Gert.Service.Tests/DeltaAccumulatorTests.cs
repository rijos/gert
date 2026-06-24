using FluentAssertions;
using Gert.Model.Agent;
using Gert.Model.Chat;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// <see cref="DeltaAccumulator"/> - the pure fold: a recorded <see cref="AgentEvent"/> sequence
/// folds to the same content/reasoning the run produced (the same fold reused at the reconnect
/// rebuild site). Discrete events (tool started/completed, finish) pass through untouched.
/// </summary>
public sealed class DeltaAccumulatorTests
{
    [Fact]
    public void Folds_a_recorded_event_sequence_to_the_final_content_and_reasoning()
    {
        var acc = new DeltaAccumulator();
        AgentEvent[] events =
        [
            new ReasoningDelta("think"),
            new TextDelta("one "),
            new ToolStarted("c1", "rag", null),
            new ToolCompleted(new ExecutedToolCall { CallId = "c1", Kind = "rag", Status = ToolCallStatus.Done }),
            new TextDelta("two"),
            new RoundCompleted(1, 0),
            new TurnFinished(new AgentResult { Content = "unused", Reasoning = "unused" }),
        ];

        foreach (var ev in events)
        {
            acc.Apply(ev);
        }

        acc.Content.Should().Be("one two");
        acc.Reasoning.Should().Be("think");
    }

    [Fact]
    public void ContentSince_slices_a_round_narration()
    {
        var acc = new DeltaAccumulator();

        acc.Apply(new TextDelta("one "));
        var mark = acc.Length;
        acc.Apply(new ReasoningDelta("r"));
        acc.Apply(new TextDelta("two"));

        acc.Content.Should().Be("one two");
        acc.ContentSince(mark).Should().Be("two");
    }
}
