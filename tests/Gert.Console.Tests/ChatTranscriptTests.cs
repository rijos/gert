using FluentAssertions;
using Gert.Console.Tui.State;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// The headless transcript model (U16): scripted <see cref="ChatEvent"/>
/// sequences must project the same structures the SPA renders — streamed
/// text with a caret, a live-then-collapsed thinking region, tool cards with
/// status/latency, citation footnotes, and the generation meta line.
/// </summary>
public sealed class ChatTranscriptTests
{
    private static ChatTranscript NewStreamingTurn(string user = "hi")
    {
        var transcript = new ChatTranscript();
        transcript.AddUser(user);
        transcript.BeginAssistant();
        transcript.Apply(new MessageStartEvent { MessageId = "m1" });
        return transcript;
    }

    [Fact]
    public void Deltas_stream_into_the_body_with_a_caret()
    {
        var transcript = NewStreamingTurn();

        transcript.Apply(new DeltaEvent { Text = "Hello " });
        transcript.Apply(new DeltaEvent { Text = "world" });

        var lines = transcript.Lines();
        lines.Should().Contain(l => l.Kind == LineKind.UserHeader && l.Text == "❯ You");
        lines.Should().Contain(l => l.Kind == LineKind.AssistantHeader);
        lines.Should().Contain(l => l.Kind == LineKind.Body && l.Text == "Hello world▌");
    }

    [Fact]
    public void Message_end_removes_the_caret_and_adds_the_meta_line()
    {
        var transcript = NewStreamingTurn();
        transcript.Apply(new DeltaEvent { Text = "Done." });

        transcript.Apply(new MessageEndEvent { TokenCount = 100, DurationMs = 2000, ContextTokens = 5000 });

        var lines = transcript.Lines();
        lines.Should().Contain(l => l.Kind == LineKind.Body && l.Text == "Done.");
        lines.Should().NotContain(l => l.Text.Contains('▌'));
        lines.Should().Contain(l => l.Kind == LineKind.Meta && l.Text == "100 tok · 50 tok/s");
    }

    [Fact]
    public void Thinking_is_expanded_while_streaming_then_collapses()
    {
        var transcript = NewStreamingTurn();

        transcript.Apply(new ReasoningEvent { Text = "let me think\nstep two" });
        var live = transcript.Lines();
        live.Should().Contain(l => l.Kind == LineKind.ThinkingHeader && l.Text.Contains('▾') && l.Text.Contains('…'));
        live.Should().Contain(l => l.Kind == LineKind.Thinking && l.Text.Contains("step two"));

        transcript.Apply(new MessageEndEvent { TokenCount = 1 });
        var done = transcript.Lines();
        done.Should().Contain(l => l.Kind == LineKind.ThinkingHeader && l.Text.Contains('▸'));
        done.Should().NotContain(l => l.Kind == LineKind.Thinking);
    }

    [Fact]
    public void Toggling_the_thinking_region_reopens_it()
    {
        var transcript = NewStreamingTurn();
        transcript.Apply(new ReasoningEvent { Text = "hidden reasoning" });
        transcript.Apply(new MessageEndEvent { TokenCount = 1 });

        var header = transcript.Lines().Single(l => l.IsRegionHeader && l.Kind == LineKind.ThinkingHeader);
        transcript.ToggleRegion(header.RegionId!);

        transcript.Lines().Should().Contain(l => l.Kind == LineKind.Thinking && l.Text.Contains("hidden reasoning"));
    }

    [Fact]
    public void Tool_call_renders_a_running_card_then_fills_with_the_result()
    {
        var transcript = NewStreamingTurn();

        transcript.Apply(new ToolCallEvent
        {
            Id = "c1",
            Kind = "grep",
            Status = ToolCallStatus.Running,
            Request = new Dictionary<string, object?> { ["pattern"] = "TODO" },
        });
        transcript.Lines().Should().Contain(l =>
            l.Kind == LineKind.ToolHeader && l.Text.Contains("◌ grep") && l.Text.Contains("TODO"));

        transcript.Apply(new ToolResultEvent
        {
            Id = "c1",
            Kind = "grep",
            Status = ToolCallStatus.Done,
            LatencyMs = 42,
            Stdout = "src/a.cs:3: // TODO fix",
        });

        var header = transcript.Lines().Single(l => l.Kind == LineKind.ToolHeader);
        header.Text.Should().Contain("● grep").And.Contain("42ms");

        // Body is collapsed by default; Enter expands the stdout rows.
        transcript.ToggleRegion(header.RegionId!);
        transcript.Lines().Should().Contain(l =>
            l.Kind == LineKind.ToolBody && l.Text.Contains("src/a.cs:3"));
    }

    [Fact]
    public void Failed_tool_headers_render_as_errors()
    {
        var transcript = NewStreamingTurn();
        transcript.Apply(new ToolCallEvent { Id = "c1", Kind = "shell", Status = ToolCallStatus.Running });

        transcript.Apply(new ToolResultEvent { Id = "c1", Kind = "shell", Status = ToolCallStatus.Error });

        transcript.Lines().Should().Contain(l => l.Kind == LineKind.Error && l.Text.Contains("✗ shell"));
    }

    [Fact]
    public void Citations_become_footnotes()
    {
        var transcript = NewStreamingTurn();
        transcript.Apply(new DeltaEvent { Text = "Per the report [1]." });

        transcript.Apply(new CitationEvent { Ordinal = 1, Label = "report.pdf", Locator = "p.4" });
        transcript.Apply(new MessageEndEvent { TokenCount = 5 });

        transcript.Lines().Should().Contain(l =>
            l.Kind == LineKind.Citation && l.Text == "[1] report.pdf — p.4");
    }

    [Fact]
    public void Cancelled_turns_show_the_stopped_marker()
    {
        var transcript = NewStreamingTurn();
        transcript.Apply(new DeltaEvent { Text = "partial" });

        transcript.Apply(new CancelledEvent { TokenCount = 3 });

        var lines = transcript.Lines();
        lines.Should().Contain(l => l.Kind == LineKind.Meta && l.Text == "Stopped");
        lines.Should().NotContain(l => l.Text.Contains('▌'));
    }

    [Fact]
    public void Errors_render_as_error_lines()
    {
        var transcript = NewStreamingTurn();

        transcript.Apply(new ErrorEvent { Message = "model unavailable" });

        transcript.Lines().Should().Contain(l => l.Kind == LineKind.Error && l.Text == "model unavailable");
        transcript.Streaming.Should().BeFalse();
    }

    [Fact]
    public void Code_fences_classify_as_code_lines()
    {
        var transcript = NewStreamingTurn();

        transcript.Apply(new DeltaEvent { Text = "Look:\n```cs\nvar x = 1;\n```\ndone" });
        transcript.Apply(new MessageEndEvent());

        var lines = transcript.Lines();
        lines.Should().Contain(l => l.Kind == LineKind.Code && l.Text == "var x = 1;");
        lines.Should().Contain(l => l.Kind == LineKind.Body && l.Text == "done");
    }

    [Fact]
    public void Message_end_context_tokens_feed_the_usage_model()
    {
        var transcript = new ChatTranscript { ContextCapacity = 10_000 };
        transcript.AddUser("q");
        transcript.BeginAssistant();

        transcript.Apply(new MessageEndEvent { TokenCount = 200, DurationMs = 4000, ContextTokens = 8000 });

        transcript.Usage.Should().NotBeNull();
        transcript.Usage!.Band.Should().Be(1, "8000/10000 = 80% is the amber band");
        transcript.Usage.TokensPerSecond.Should().Be(50);
        transcript.Usage.Display().Should().Contain("8K/10K").And.Contain("50 tok/s");
    }

    [Fact]
    public void Changed_fires_on_every_mutation()
    {
        var transcript = new ChatTranscript();
        var fired = 0;
        transcript.Changed += () => fired++;

        transcript.AddUser("a");
        transcript.BeginAssistant();
        transcript.Apply(new DeltaEvent { Text = "x" });

        fired.Should().Be(3);
    }

    [Fact]
    public void Rebuild_replays_a_persisted_thread()
    {
        var transcript = new ChatTranscript { ContextCapacity = 1000 };
        var thread = new ConversationThread
        {
            Conversation = new Conversation
            {
                Id = "c1",
                Title = "t",
                ModelId = "default",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            Messages =
            [
                new Message
                {
                    Id = "m1",
                    ConversationId = "c1",
                    Role = MessageRole.User,
                    Content = "question",
                    Status = MessageStatus.Complete,
                    Seq = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                new Message
                {
                    Id = "m2",
                    ConversationId = "c1",
                    Role = MessageRole.Assistant,
                    Content = "answer",
                    Reasoning = "thought",
                    TokenCount = 7,
                    ContextTokens = 500,
                    Status = MessageStatus.Complete,
                    Seq = 2,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            ],
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "tc1",
                    MessageId = "m2",
                    Kind = "rag",
                    Status = ToolCallStatus.Done,
                    LatencyMs = 12,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            ],
            Citations =
            [
                new Citation
                {
                    Id = "ci1",
                    MessageId = "m2",
                    Ordinal = 1,
                    SourceType = CitationSourceType.Document,
                    Label = "doc.md",
                },
            ],
        };

        transcript.Rebuild(thread);

        var lines = transcript.Lines();
        lines.Should().Contain(l => l.Kind == LineKind.Body && l.Text == "question");
        lines.Should().Contain(l => l.Kind == LineKind.Body && l.Text == "answer");
        lines.Should().Contain(l => l.Kind == LineKind.ThinkingHeader);
        lines.Should().Contain(l => l.Kind == LineKind.ToolHeader && l.Text.Contains("rag"));
        lines.Should().Contain(l => l.Kind == LineKind.Citation && l.Text.StartsWith("[1] doc.md"));
        transcript.Usage!.Used.Should().Be(500);
        transcript.Streaming.Should().BeFalse();
    }
}
