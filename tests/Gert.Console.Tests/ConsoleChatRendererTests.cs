using System.Globalization;
using FluentAssertions;
using Gert.Console;
using Gert.Model;
using Gert.Model.Events;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// The Console's analog of the Api's SSE rendering (testing.md §7): a synthetic
/// <see cref="ChatEvent"/> stream is fed to <see cref="ConsoleChatRenderer"/> with
/// in-memory writers, and the rendered text is asserted — the deltas concatenate,
/// the tool/citation lines appear, the token count prints, and an
/// <see cref="ErrorEvent"/> goes to the error writer.
/// </summary>
public sealed class ConsoleChatRendererTests
{
    [Fact]
    public async Task Renders_full_stream_to_stdout_with_tool_citation_and_token_count()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var renderer = new ConsoleChatRenderer(output, error);

        var stream = ToAsync(
            new MessageStartEvent { MessageId = "m1" },
            new DeltaEvent { Text = "Hello, " },
            new DeltaEvent { Text = "world." },
            new ToolCallEvent { Id = "t1", Kind = "rag", Status = ToolCallStatus.Running },
            new ToolResultEvent
            {
                Id = "t1",
                Kind = "rag",
                Status = ToolCallStatus.Done,
                Hits =
                [
                    new ToolResultHit { Doc = "report.md", Page = "1", Score = 0.9 },
                    new ToolResultHit { Doc = "notes.md", Page = "2", Score = 0.8 },
                ],
            },
            new CitationEvent { Ordinal = 1, Label = "report.md p.1" },
            new MessageEndEvent { TokenCount = 42 });

        await renderer.RenderAsync(stream);

        var text = output.ToString();

        // Deltas concatenate inline (the typewriter effect).
        text.Should().Contain("Hello, world.");

        // Tool-call + tool-result lines appear.
        text.Should().Contain("» tool: rag");
        text.Should().Contain("✓ rag (2 hits)");

        // Citation line.
        text.Should().Contain("[1] report.md p.1");

        // Final token count.
        text.Should().Contain("[42 tokens]");

        // Nothing went to the error writer on the happy path.
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Error_event_is_written_to_the_error_writer_not_stdout()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var renderer = new ConsoleChatRenderer(output, error);

        var stream = ToAsync(
            new MessageStartEvent { MessageId = "m1" },
            new ErrorEvent { Message = "upstream exploded" });

        await renderer.RenderAsync(stream);

        error.ToString().Should().Contain("upstream exploded");
        output.ToString().Should().NotContain("upstream exploded");
    }

    [Fact]
    public void Message_end_without_token_count_renders_done_marker()
    {
        var output = new StringWriter();
        var renderer = new ConsoleChatRenderer(output, new StringWriter());

        renderer.Render(new MessageEndEvent());

        output.ToString().Should().Contain("[done]");
    }

    [Fact]
    public void Citation_uses_invariant_culture_for_the_ordinal()
    {
        var output = new StringWriter();
        var renderer = new ConsoleChatRenderer(output, new StringWriter());

        renderer.Render(new CitationEvent { Ordinal = 3, Label = "x" });

        output.ToString().Should().Contain("[3] x");
        3.ToString(CultureInfo.InvariantCulture).Should().Be("3");
    }

    private static async IAsyncEnumerable<ChatEvent> ToAsync(params ChatEvent[] events)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            yield return e;
        }
    }
}
