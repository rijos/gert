using System.Runtime.CompilerServices;
using FluentAssertions;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Model.Rag;
using Gert.Service.Chat;
using Gert.Service.Chat.Bus;
using Gert.Database;
using Gert.Service.External;
using Gert.Service.Tools;
using Gert.Testing.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// The detached tool loop (replaces the ChatService.RunAsync suites): event
/// ORDER is unchanged from the old pipeline — message_start → tool_call(running)
/// → tool_result(done) → deltas → citation → message_end — but is now asserted
/// on the PUBLISHED <see cref="TurnEvent"/>s, plus the new invariants: the
/// persist-before-publish protocol, live tool/citation persistence with
/// provenance, the entitlement SNAPSHOT ceiling, error rows persisting as
/// status=error (a deliberate behavior change), and the wall-clock cap.
/// </summary>
public sealed class TurnRunnerTests
{
    private const string Pid = "default";
    private const string Conv = "conv-1";
    private const string AssistantId = "assistant-msg-1";

    private readonly IChatRepository _repo = Substitute.For<IChatRepository>();
    private readonly IRagRepository _ragRepo = Substitute.For<IRagRepository>();
    private readonly IChatDatabaseProvider _chatProvider = Substitute.For<IChatDatabaseProvider>();
    private readonly IRagDatabaseProvider _ragProvider = Substitute.For<IRagDatabaseProvider>();
    private readonly IConversationBus _bus = Substitute.For<IConversationBus>();

    private readonly List<TurnEvent> _published = [];
    private readonly List<TurnEventRecord> _appended = [];
    private readonly List<string> _protocol = []; // "append:N" / "publish:N"
    private readonly List<ToolCall> _toolRows = [];
    private readonly List<(string Content, MessageStatus Status, int? TokenCount)> _streamUpdates = [];
    private readonly List<(string Content, MessageStatus Status, int? TokenCount, string? Reasoning, long? DurationMs, int? ContextTokens)> _finalized = [];
    private IReadOnlyList<Citation>? _insertedCitations;
    private long _seq = 2; // plan time allocated 1 (user) and 2 (assistant)

    public TurnRunnerTests()
    {
        _chatProvider
            .OpenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_repo);
        _ragProvider
            .OpenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ragRepo);

        _repo.AllocateSeqAsync(Conv, Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref _seq));
        _repo.AppendTurnEventAsync(Arg.Any<TurnEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci =>
            {
                var record = ci.Arg<TurnEventRecord>();
                _appended.Add(record);
                _protocol.Add($"append:{record.Seq}");
            });
        _repo.InsertToolCallAsync(Arg.Any<ToolCall>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => _toolRows.Add(ci.Arg<ToolCall>()));
        _repo.InsertCitationsAsync(Arg.Any<IReadOnlyList<Citation>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => _insertedCitations = ci.Arg<IReadOnlyList<Citation>>());
        _repo.UpdateMessageStreamAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessageStatus>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => _streamUpdates.Add((ci.ArgAt<string>(1), ci.Arg<MessageStatus>(), ci.ArgAt<int?>(3))));
        _repo.FinalizeMessageAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessageStatus>(), Arg.Any<int?>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci =>
            {
                // Terminal transitions land in the same stream-update log the
                // lifecycle assertions read, plus the full metrics tuple.
                _streamUpdates.Add((ci.ArgAt<string>(1), ci.Arg<MessageStatus>(), ci.ArgAt<int?>(3)));
                _finalized.Add((
                    ci.ArgAt<string>(1),
                    ci.Arg<MessageStatus>(),
                    ci.ArgAt<int?>(3),
                    ci.ArgAt<string?>(4),
                    ci.ArgAt<long?>(5),
                    ci.ArgAt<int?>(6)));
            });

        _bus.When(b => b.Publish(Arg.Any<ConversationTopic>(), Arg.Any<TurnEvent>()))
            .Do(ci =>
            {
                var evt = ci.ArgAt<TurnEvent>(1);
                _published.Add(evt);
                _protocol.Add($"publish:{evt.Seq}");
            });

        _ragRepo
            .HybridSearchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<float>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RetrievedChunk
                {
                    Chunk = new Chunk { Id = 1, DocumentId = "doc-1", Ordinal = 0, Content = "sqlite-vec wins", Page = "p.1" },
                    Document = new Document
                    {
                        Id = "doc-1",
                        Filename = "bench.pdf",
                        Mime = "application/pdf",
                        SizeBytes = 10,
                        Status = DocumentStatus.Ready,
                        CreatedAt = DateTimeOffset.UtcNow,
                    },
                    Score = 0.91,
                },
            });
    }

    private IReadOnlyList<ChatEvent> Events => _published.Select(p => p.Event).ToList();

    private TurnRunner NewRunner(
        IChatModelClient model,
        IEnumerable<ITool>? tools = null,
        TurnOptions? options = null,
        TimeProvider? clock = null,
        ITurnCancellation? cancellation = null) =>
        new(_chatProvider, model, _bus, tools ?? [], Options.Create(options ?? new TurnOptions()),
            clock ?? TimeProvider.System,
            cancellation ?? new TurnCancellation(
                Options.Create(options ?? new TurnOptions()), clock ?? TimeProvider.System),
            NullLogger<TurnRunner>.Instance);

    private static TurnJob NewJob(
        string userContent,
        IReadOnlyList<ITool>? offered = null,
        IReadOnlySet<string>? allowed = null) => new()
    {
        Iss = "https://idp.example",
        Sub = "sub-123",
        Username = "tester",
        AllowedToolIds = allowed ?? offered?.Select(t => t.Id).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(),
        Pid = Pid,
        ConversationId = Conv,
        UserMessageId = "user-msg-1",
        AssistantMessageId = AssistantId,
        AssistantSeq = 2,
        ModelId = "default",
        History = [new ChatModelMessage { Role = "user", Content = userContent }],
        ToolIds = offered?.Select(t => t.Id).ToList() ?? [],
        Tools = offered?.Select(t => new ChatToolSpec
        {
            Name = t.Name,
            Description = t.Description,
            ParametersSchema = t.ParametersSchema,
        }).ToList() ?? [],
    };

    [Fact]
    public async Task No_tool_path_emits_start_then_deltas_then_end_and_finalizes_complete()
    {
        await NewRunner(new FakeChatModel()).RunAsync(NewJob("hello"));

        Events.First().Should().BeOfType<MessageStartEvent>()
            .Which.MessageId.Should().Be(AssistantId);
        Events.Last().Should().BeOfType<MessageEndEvent>();
        Events.Skip(1).SkipLast(1).Should().AllBeOfType<DeltaEvent>();

        // The row finalised complete, content = the full streamed text.
        var final = _streamUpdates.Last();
        final.Status.Should().Be(MessageStatus.Complete);
        final.Content.Should().Be(string.Concat(Events.OfType<DeltaEvent>().Select(d => d.Text)));
    }

    [Fact]
    public async Task Every_published_event_is_persisted_first_with_the_same_seq()
    {
        await NewRunner(new FakeChatModel()).RunAsync(NewJob("hello"));

        _published.Should().NotBeEmpty();

        // Same events in the durable log, seq-for-seq and type-for-type…
        _appended.Select(a => a.Seq).Should().Equal(_published.Select(p => p.Seq));
        _appended.Select(a => a.Type)
            .Should().Equal(_published.Select(p => p.Event.Type.ToWireName()));

        // …and the protocol is strictly persist-before-publish (the splice's
        // gap-free guarantee depends on this order).
        foreach (var evt in _published)
        {
            _protocol.IndexOf($"append:{evt.Seq}")
                .Should().BeLessThan(_protocol.IndexOf($"publish:{evt.Seq}"),
                    $"event seq {evt.Seq} must be durable before it is published");
        }

        // Seqs continue monotonically after the plan-time rows (user=1, assistant=2).
        _published.Select(p => p.Seq).Should().BeInAscendingOrder()
            .And.OnlyHaveUniqueItems();
        _published[0].Seq.Should().Be(3);
    }

    [Fact]
    public async Task Rag_tool_loop_emits_full_event_sequence_and_persists_provenance()
    {
        var user = new TestUserContext { AllowedTools = new HashSet<string>(["rag"], StringComparer.Ordinal) };
        var ragTool = new RagTool(_ragProvider, new FakeEmbeddings(), user);

        await NewRunner(new FakeChatModel(), [ragTool])
            .RunAsync(NewJob("search my docs about qdrant", [ragTool]));

        var types = Events.Select(e => e.GetType().Name).ToArray();

        // Unchanged order contract: start → tool events → deltas → citation → end.
        types.First().Should().Be(nameof(MessageStartEvent));
        types.Last().Should().Be(nameof(MessageEndEvent));

        var toolCall = Events.OfType<ToolCallEvent>().Single();
        toolCall.Kind.Should().Be("rag");
        toolCall.Status.Should().Be(ToolCallStatus.Running);

        var toolResult = Events.OfType<ToolResultEvent>().Single();
        toolResult.Status.Should().Be(ToolCallStatus.Done);
        toolResult.Hits.Should().NotBeNullOrEmpty();

        Array.IndexOf(types, nameof(ToolResultEvent))
            .Should().BeLessThan(Array.IndexOf(types, nameof(DeltaEvent)));

        string.Concat(Events.OfType<DeltaEvent>().Select(d => d.Text))
            .Should().Be("Based on your docs, sqlite-vec wins [1].");

        var citation = Events.OfType<CitationEvent>().Single();
        citation.Ordinal.Should().Be(1);
        citation.Label.Should().Be("bench.pdf · p.1");

        // The tool row persisted LIVE with kind + latency…
        var toolRow = _toolRows.Single();
        toolRow.Kind.Should().Be("rag");
        toolRow.Status.Should().Be(ToolCallStatus.Done);
        toolRow.LatencyMs.Should().NotBeNull();
        toolRow.MessageId.Should().Be(AssistantId);

        // …and the citation row carries its provenance (the tree:
        // message → tool_call → citation) plus the message binding.
        _insertedCitations.Should().ContainSingle();
        _insertedCitations![0].MessageId.Should().Be(AssistantId);
        _insertedCitations[0].ToolCallId.Should().Be(toolRow.Id);
        _insertedCitations[0].Ordinal.Should().Be(1);
    }

    [Fact]
    public async Task Entitlement_snapshot_blocks_execution_even_when_the_spec_reached_the_model()
    {
        // The sandbox spec is (improperly) offered, but the plan-time snapshot
        // does not include it — the off-thread second-line defence must refuse.
        var sandbox = new SandboxTool(new StubSandbox());

        await NewRunner(new FakeChatModel(), [sandbox])
            .RunAsync(NewJob(
                "run python to add two and two",
                offered: [sandbox],
                allowed: new HashSet<string>(["rag"], StringComparer.Ordinal)));

        var toolResult = Events.OfType<ToolResultEvent>().Single();
        toolResult.Status.Should().Be(ToolCallStatus.Error);

        _toolRows.Single().Status.Should().Be(ToolCallStatus.Error);
    }

    [Fact]
    public async Task Model_fault_finalizes_the_row_as_error_and_emits_a_terminal_error_event()
    {
        await NewRunner(new ExplodingModel()).RunAsync(NewJob("hello"));

        // Behavior change vs the old pipeline (which persisted nothing): the
        // partial content survives on an error row, and the log ends in `error`.
        Events.First().Should().BeOfType<MessageStartEvent>();
        Events.OfType<DeltaEvent>().Single().Text.Should().Be("partial ");
        Events.Last().Should().BeOfType<ErrorEvent>()
            .Which.Message.Should().Contain("model exploded");
        Events.Should().NotContain(e => e is MessageEndEvent);

        var final = _streamUpdates.Last();
        final.Status.Should().Be(MessageStatus.Error);
        final.Content.Should().Be("partial ");
    }

    [Fact]
    public async Task Turn_exceeding_the_wall_clock_cap_finalizes_as_error()
    {
        var options = new TurnOptions { MaxTurnDuration = TimeSpan.FromMilliseconds(200) };

        await NewRunner(new NeverFinishingModel(), options: options).RunAsync(NewJob("hello"));

        Events.Last().Should().BeOfType<ErrorEvent>()
            .Which.Message.Should().Contain("exceeded");
        _streamUpdates.Last().Status.Should().Be(MessageStatus.Error);
    }

    [Fact]
    public async Task User_cancel_finalizes_as_cancelled_with_the_partial_content()
    {
        var registry = new TurnCancellation(Options.Create(new TurnOptions()), TimeProvider.System);
        var job = NewJob("hello");
        var key = TurnKey.From(job);

        var turn = NewRunner(new NeverFinishingModel(), cancellation: registry).RunAsync(job);

        // Wait for the turn to register + open (message_start), then stop it.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_published.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        registry.Cancel(key).Should().BeTrue();
        await turn; // a user cancel is a normal outcome — no throw

        // The row finalised cancelled with everything that streamed, and the log
        // ends in the terminal `cancelled` event — never an error.
        var final = _streamUpdates.Last();
        final.Status.Should().Be(MessageStatus.Cancelled);
        final.Content.Should().Be("thinking… ");
        Events.Last().Should().BeOfType<CancelledEvent>();
        Events.Should().NotContain(e => e is ErrorEvent);
        Events.Should().NotContain(e => e is MessageEndEvent);
    }

    [Fact]
    public async Task Host_shutdown_finalizes_as_error_and_rethrows()
    {
        using var host = new CancellationTokenSource();
        var turn = NewRunner(new NeverFinishingModel()).RunAsync(NewJob("hello"), host.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_published.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        host.Cancel();

        // Shutdown propagates (the worker observes it); the row reads error.
        await turn.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        _streamUpdates.Last().Status.Should().Be(MessageStatus.Error);
        Events.Last().Should().BeOfType<ErrorEvent>()
            .Which.Message.Should().Contain("shutdown");
    }

    [Fact]
    public async Task A_tool_that_returns_an_artifact_emits_it_after_the_tool_result()
    {
        // Canvas artifacts come from the make/edit tools now: a tool surfaces the
        // artifact on its result and the runner emits the ArtifactEvent (the tool
        // already persisted it). The frontend upserts by id, so the same id from a
        // later edit updates the tab in place.
        var artifact = new Artifact
        {
            Id = "art-9",
            ConversationId = Conv,
            MessageId = AssistantId,
            Kind = ArtifactKind.Html,
            Name = "demo.html",
            Language = "html",
            Content = "<h1>Demo</h1>",
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var tool = new ArtifactStubTool(artifact);

        await NewRunner(new TextThenToolModel(), [tool]).RunAsync(NewJob("make me a demo page", [tool]));

        var artifactEvent = Events.OfType<ArtifactEvent>().Single();
        artifactEvent.Id.Should().Be("art-9");
        artifactEvent.Kind.Should().Be(ArtifactKind.Html);
        artifactEvent.Name.Should().Be("demo.html");
        artifactEvent.Content.Should().Be("<h1>Demo</h1>");

        // Emitted right after the tool_result that produced it, before message_end.
        var types = Events.Select(e => e.GetType().Name).ToList();
        types.IndexOf(nameof(ArtifactEvent))
            .Should().BeGreaterThan(types.IndexOf(nameof(ToolResultEvent)))
            .And.BeLessThan(types.IndexOf(nameof(MessageEndEvent)));
    }

    [Fact]
    public async Task A_plain_text_turn_with_code_fences_produces_no_artifact()
    {
        // Fenced code in the reply is just inline code — only the tools make artifacts.
        await NewRunner(new InlineCodeModel()).RunAsync(NewJob("show me some code"));

        Events.Should().NotContain(e => e is ArtifactEvent);
    }

    [Fact]
    public async Task A_streamed_tool_name_announces_a_running_card_before_the_call_completes()
    {
        var stub = new StubTool();
        await NewRunner(new AnnouncingToolModel(), [stub]).RunAsync(NewJob("go", [stub]));

        // The live-intent announce (name only, mid-stream) and the end-of-round
        // running event (now with args) share the call id — two Running tool_call
        // events for one call; the UI dedupes by id into a single card.
        var calls = Events.OfType<ToolCallEvent>().Where(e => e.Id == "call_x").ToList();
        calls.Should().HaveCount(2);
        calls.Should().OnlyContain(e => e.Status == ToolCallStatus.Running);

        // The announce precedes the tool_result (it was emitted while streaming).
        var types = Events.Select(e => e.GetType().Name).ToList();
        types.IndexOf(nameof(ToolCallEvent))
            .Should().BeLessThan(types.IndexOf(nameof(ToolResultEvent)));
    }

    [Fact]
    public async Task Tool_round_history_carries_one_assistant_message_with_all_tool_calls()
    {
        var clock = new StubTool();
        var model = new TwoCallToolModel();

        await NewRunner(model, [clock]).RunAsync(NewJob("what time is it", [clock]));

        // Round 2's upstream history (OpenAI wire shape): ONE assistant message
        // carrying BOTH tool calls of the round, then one tool result per call,
        // in call order.
        model.Requests.Should().HaveCount(2);
        var second = model.Requests[1];

        var assistant = second.Single(m => m.Role == "assistant");
        assistant.Content.Should().BeNull();
        assistant.ToolCallId.Should().BeNull();
        assistant.ToolCalls.Should().HaveCount(2);
        assistant.ToolCalls![0].Id.Should().Be("call_a");
        assistant.ToolCalls[1].Id.Should().Be("call_b");

        var results = second.Where(m => m.Role == "tool").ToList();
        results.Should().HaveCount(2);
        results[0].ToolCallId.Should().Be("call_a");
        results[1].ToolCallId.Should().Be("call_b");

        // The assistant tool-call turn precedes its results.
        second.IndexOf(assistant).Should().BeLessThan(second.IndexOf(results[0]));
    }

    [Fact]
    public async Task Round_narration_rides_back_in_the_assistant_tool_call_message()
    {
        var stub = new StubTool();
        var model = new NarratingToolModel();

        await NewRunner(model, [stub]).RunAsync(NewJob("make files", [stub]));

        // The text streamed alongside a round's tool calls is part of the
        // assistant turn (qwen narrates while it calls set_todos). Dropping it
        // makes the model believe its own work never happened and restart the
        // answer next round ("oops, I jumped the gun").
        model.Requests.Should().HaveCount(2);
        var assistant = model.Requests[1].Single(m => m.Role == "assistant" && m.ToolCalls is not null);
        assistant.Content.Should().Be("Here is file one.");
        assistant.ToolCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Runaway_tool_loop_is_bounded_and_finalizes_complete()
    {
        var stub = new StubTool();
        var model = new AlwaysToolCallingModel();

        // A model that requests a tool on EVERY round, no matter what comes back.
        // Past the cap the runner refuses the calls, winds down once with no
        // tools advertised, and when even that round emits tool calls it stops
        // calling upstream — before this brake the loop spun against vLLM until
        // MaxTurnDuration, 409-blocking the conversation the whole time.
        await NewRunner(model, [stub], new TurnOptions { MaxToolRounds = 5 })
            .RunAsync(NewJob("loop forever", [stub]));

        // 5 executed rounds + 1 refused round + 1 wind-down round = 7 upstream
        // calls, hard stop.
        model.Tools.Should().HaveCount(7);
        model.Tools.Take(6).Should().OnlyContain(t => t.Count == 1,
            "executed and refused rounds still advertise the tool");
        model.Tools[6].Should().BeEmpty("the wind-down round must not advertise tools");

        var final = _finalized.Single();
        final.Status.Should().Be(MessageStatus.Complete);
    }

    [Fact]
    public async Task Capped_round_refuses_calls_with_synthetic_results_keeping_the_wire_format()
    {
        var stub = new StubTool();
        var model = new AlwaysToolCallingModel();

        await NewRunner(model, [stub], new TurnOptions { MaxToolRounds = 5 })
            .RunAsync(NewJob("loop forever", [stub]));

        // The refused round still answers each call in the upstream history —
        // ONE assistant message carrying the round's narration + tool calls,
        // then a budget-exhausted tool result per call — so the wind-down
        // request stays wire-format valid and the model sees its own words.
        var windDown = model.Requests[6];
        var assistant = windDown.Last(m => m.Role == "assistant");
        assistant.ToolCalls.Should().ContainSingle();
        assistant.Content.Should().Contain("round 6");

        var result = windDown.Last(m => m.Role == "tool");
        result.ToolCallId.Should().Be(assistant.ToolCalls![0].Id);
        result.Content.Should().Contain("tool budget exhausted");

        // Five executed rows persisted done; the refused round's row is an error.
        _toolRows.Count(r => r.Status == ToolCallStatus.Done).Should().Be(5);
        _toolRows.Count(r => r.Status == ToolCallStatus.Error).Should().Be(1);

        // The cap-trip is VISIBLE: the refused call's result event carries the
        // budget message for the card's error line (turn-budgets.md §6).
        var refused = Events.OfType<ToolResultEvent>().Single(e => e.Status == ToolCallStatus.Error);
        refused.Error.Should().Contain("tool budget exhausted");
    }

    [Fact]
    public async Task Per_round_max_tokens_defaults_and_clamps_to_the_configured_bound()
    {
        var model = new RequestCapturingModel();
        var options = new TurnOptions { MaxTokensPerRound = 100 };

        // Unset → the bound is the default; over the bound → clamped; under → kept.
        await NewRunner(model, options: options).RunAsync(NewJob("hello"));
        await NewRunner(model, options: options).RunAsync(NewJob("hello") with { MaxTokens = 500 });
        await NewRunner(model, options: options).RunAsync(NewJob("hello") with { MaxTokens = 50 });

        model.MaxTokens.Should().Equal(100, 100, 50);

        // Disabled (0) → requests pass through untouched, unset stays unset.
        var off = new TurnOptions { MaxTokensPerRound = 0 };
        await NewRunner(model, options: off).RunAsync(NewJob("hello") with { MaxTokens = 500 });
        await NewRunner(model, options: off).RunAsync(NewJob("hello"));
        model.MaxTokens.Skip(3).Should().Equal(500, null);
    }

    [Fact]
    public async Task Hung_tool_call_times_out_with_a_visible_error_and_the_turn_completes()
    {
        var hung = new HangingTool();
        var options = new TurnOptions { ToolCallTimeout = TimeSpan.FromMilliseconds(50) };

        await NewRunner(new TextThenToolModel(), [hung], options)
            .RunAsync(NewJob("hang forever", [hung]));

        // The call failed visibly — card error text names the timeout — and the
        // turn went on to a complete final answer instead of dying with it.
        var result = Events.OfType<ToolResultEvent>().Single();
        result.Status.Should().Be(ToolCallStatus.Error);
        result.Error.Should().Contain("timed out");

        _finalized.Single().Status.Should().Be(MessageStatus.Complete);
    }

    [Fact]
    public async Task Deltas_within_the_flush_window_coalesce_into_one_event()
    {
        // A clock that never advances: neither the time nor (with the default
        // 512-char cap) the size threshold fires, so ALL chunks coalesce and the
        // single delta is the end-of-stream boundary flush.
        await NewRunner(new ScriptedModel("one ", "two ", "three"), clock: new ManualClock())
            .RunAsync(NewJob("hello"));

        var delta = Events.OfType<DeltaEvent>().Should().ContainSingle().Subject;
        delta.Text.Should().Be("one two three");
        _streamUpdates.Last().Content.Should().Be("one two three");
    }

    [Fact]
    public async Task Pending_deltas_flush_when_the_interval_elapses()
    {
        var clock = new ManualClock();
        var options = new TurnOptions { DeltaFlushInterval = TimeSpan.FromMilliseconds(150) };

        // The model advances the clock 200ms between chunks: each arrival past
        // the window flushes what was buffered BEFORE it plus itself.
        await NewRunner(new ClockAdvancingModel(clock, TimeSpan.FromMilliseconds(200)), options: options, clock: clock)
            .RunAsync(NewJob("hello"));

        Events.OfType<DeltaEvent>().Select(d => d.Text).Should().Equal("a b ", "c");
    }

    [Fact]
    public async Task Pending_deltas_flush_at_the_size_cap_even_mid_interval()
    {
        var options = new TurnOptions
        {
            DeltaFlushInterval = TimeSpan.FromHours(1),
            DeltaFlushMaxChars = 4,
        };

        await NewRunner(new ScriptedModel("ab", "cd", "ef"), options: options, clock: new ManualClock())
            .RunAsync(NewJob("hello"));

        // "ab"+"cd" hits the 4-char cap; "ef" rides the end-of-stream boundary.
        Events.OfType<DeltaEvent>().Select(d => d.Text).Should().Equal("abcd", "ef");
    }

    [Fact]
    public async Task Round_text_flushes_before_the_rounds_tool_events()
    {
        var stub = new StubTool();
        var options = new TurnOptions { DeltaFlushInterval = TimeSpan.FromHours(1) };

        await NewRunner(new TextThenToolModel(), [stub], options, new ManualClock())
            .RunAsync(NewJob("hello", [stub]));

        var types = Events.Select(e => e.GetType().Name).ToList();

        // Round 1's text precedes its tool_call; round 2's text precedes message_end.
        Events.OfType<DeltaEvent>().Select(d => d.Text).Should().Equal("checking ", "done");
        types.IndexOf(nameof(DeltaEvent))
            .Should().BeLessThan(types.IndexOf(nameof(ToolCallEvent)));
        types.LastIndexOf(nameof(DeltaEvent))
            .Should().BeGreaterThan(types.IndexOf(nameof(ToolResultEvent)))
            .And.BeLessThan(types.IndexOf(nameof(MessageEndEvent)));
    }

    [Fact]
    public async Task Per_chunk_flushing_is_preserved_when_the_interval_is_zero()
    {
        var options = new TurnOptions { DeltaFlushInterval = TimeSpan.Zero };

        await NewRunner(new ScriptedModel("a", "b", "c"), options: options, clock: new ManualClock())
            .RunAsync(NewJob("hello"));

        Events.OfType<DeltaEvent>().Select(d => d.Text).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Reasoning_deltas_coalesce_and_precede_content_and_persist_on_finalize()
    {
        var model = new ScriptedChunkModel(
            new ChatModelChunk { ReasoningDelta = "hmm, " },
            new ChatModelChunk { ReasoningDelta = "thinking" },
            new ChatModelChunk { TextDelta = "Answer." });

        await NewRunner(model, clock: new ManualClock()).RunAsync(NewJob("hello"));

        // One coalesced reasoning event BEFORE the (coalesced) delta.
        var types = Events.Select(e => e.GetType().Name).ToList();
        var reasoningEvent = Events.OfType<ReasoningEvent>().Should().ContainSingle().Subject;
        reasoningEvent.Text.Should().Be("hmm, thinking");
        types.IndexOf(nameof(ReasoningEvent))
            .Should().BeLessThan(types.IndexOf(nameof(DeltaEvent)));

        // The full reasoning text rides the finalize, content stays pure.
        var final = _finalized.Should().ContainSingle().Subject;
        final.Status.Should().Be(MessageStatus.Complete);
        final.Reasoning.Should().Be("hmm, thinking");
        final.Content.Should().Be("Answer.");
    }

    [Fact]
    public async Task Duration_counts_stream_spans_only_and_excludes_tool_execution()
    {
        var clock = new ManualClock();
        var stub = new ClockAdvancingTool(clock, TimeSpan.FromMilliseconds(500));
        var model = new ClockAdvancingToolModel(clock, TimeSpan.FromMilliseconds(100));

        await NewRunner(model, [stub], clock: clock).RunAsync(NewJob("hello", [stub]));

        // Two rounds × 100ms of stream time; the 500ms tool execution between
        // them must NOT count toward generation duration.
        var end = Events.OfType<MessageEndEvent>().Single();
        end.DurationMs.Should().Be(200);
        _finalized.Single().DurationMs.Should().Be(200);
    }

    [Fact]
    public async Task Context_tokens_combine_final_prompt_and_completion_counts()
    {
        var model = new ScriptedChunkModel(
            new ChatModelChunk { TextDelta = "hi" },
            new ChatModelChunk { TokenCount = 56, PromptTokenCount = 1000 });

        await NewRunner(model, clock: new ManualClock()).RunAsync(NewJob("hello"));

        var end = Events.OfType<MessageEndEvent>().Single();
        end.TokenCount.Should().Be(56);
        end.ContextTokens.Should().Be(1056);
        _finalized.Single().ContextTokens.Should().Be(1056);
    }

    // ---- scripted models -----------------------------------------------------

    /// <summary>Streams the given chunks verbatim, then stops.</summary>
    private sealed class ScriptedChunkModel(params ChatModelChunk[] chunks) : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in chunks)
            {
                yield return chunk;
            }

            await Task.Yield();
        }
    }

    /// <summary>
    /// Round 1: advances the clock by <paramref name="streamSpan"/> mid-stream,
    /// then requests a tool. Round 2: advances again and answers.
    /// </summary>
    private sealed class ClockAdvancingToolModel(ManualClock clock, TimeSpan streamSpan) : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            clock.Advance(streamSpan);
            if (!request.Messages.Any(m => m.Role == "tool"))
            {
                yield return new ChatModelChunk
                {
                    ToolCall = new ChatModelToolCall { Id = "call_1", Name = "stub_tool", ArgumentsJson = "{}" },
                };
            }
            else
            {
                yield return new ChatModelChunk { TextDelta = "done" };
            }

            await Task.Yield();
        }
    }

    /// <summary>A stub tool whose execution advances the manual clock (the tool gap).</summary>
    private sealed class ClockAdvancingTool(ManualClock clock, TimeSpan executionSpan) : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "a stub";

        public string ParametersSchema => """{"type":"object"}""";

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            clock.Advance(executionSpan);
            return Task.FromResult(new ToolResult { Success = true, ResultJson = "{}" });
        }
    }

    /// <summary>A TimeProvider whose timestamp only moves when the test advances it.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan by) =>
            _timestamp += (long)(by.TotalSeconds * TimestampFrequency);
    }

    /// <summary>Streams the given text chunks, then stops.</summary>
    private sealed class ScriptedModel(params string[] chunks) : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in chunks)
            {
                yield return new ChatModelChunk { TextDelta = chunk };
            }

            await Task.Yield();
        }
    }

    /// <summary>Streams three chunks, advancing the manual clock between them.</summary>
    private sealed class ClockAdvancingModel(ManualClock clock, TimeSpan step) : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatModelChunk { TextDelta = "a " };
            clock.Advance(step);
            yield return new ChatModelChunk { TextDelta = "b " };
            clock.Advance(step);
            yield return new ChatModelChunk { TextDelta = "c" };
            await Task.Yield();
        }
    }

    /// <summary>Round 1: text then a tool call. Round 2: a final answer.</summary>
    private sealed class TextThenToolModel : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!request.Messages.Any(m => m.Role == "tool"))
            {
                yield return new ChatModelChunk { TextDelta = "checking " };
                yield return new ChatModelChunk
                {
                    ToolCall = new ChatModelToolCall { Id = "call_1", Name = "stub_tool", ArgumentsJson = "{}" },
                };
            }
            else
            {
                yield return new ChatModelChunk { TextDelta = "done" };
            }

            await Task.Yield();
        }
    }

    /// <summary>Round 1: two tool calls. Round 2: a final answer. Captures each request's messages.</summary>
    private sealed class TwoCallToolModel : IChatModelClient
    {
        public List<List<ChatModelMessage>> Requests { get; } = [];

        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Messages.ToList());

            if (!request.Messages.Any(m => m.Role == "tool"))
            {
                yield return new ChatModelChunk
                {
                    ToolCall = new ChatModelToolCall { Id = "call_a", Name = "stub_tool", ArgumentsJson = "{}" },
                };
                yield return new ChatModelChunk
                {
                    ToolCall = new ChatModelToolCall { Id = "call_b", Name = "stub_tool", ArgumentsJson = """{"x":1}""" },
                };
            }
            else
            {
                yield return new ChatModelChunk { TextDelta = "done" };
            }

            await Task.Yield();
        }
    }

    /// <summary>Round 1 streams narration text AND a tool call; round 2 finishes.</summary>
    private sealed class NarratingToolModel : IChatModelClient
    {
        public List<List<ChatModelMessage>> Requests { get; } = [];

        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Messages.ToList());

            if (!request.Messages.Any(m => m.Role == "tool"))
            {
                yield return new ChatModelChunk { TextDelta = "Here is file one." };
                yield return new ChatModelChunk
                {
                    ToolCall = new ChatModelToolCall { Id = "call_a", Name = "stub_tool", ArgumentsJson = "{}" },
                };
            }
            else
            {
                yield return new ChatModelChunk { TextDelta = " And file two." };
            }

            await Task.Yield();
        }
    }

    /// <summary>
    /// Requests a tool on EVERY round, no matter what came back — the runaway
    /// tool loop. Captures each request's messages and advertised tools.
    /// </summary>
    private sealed class AlwaysToolCallingModel : IChatModelClient
    {
        public List<List<ChatModelMessage>> Requests { get; } = [];

        public List<IReadOnlyList<ChatToolSpec>> Tools { get; } = [];

        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Messages.ToList());
            Tools.Add(request.Tools);

            yield return new ChatModelChunk { TextDelta = $"round {Requests.Count} " };
            yield return new ChatModelChunk
            {
                ToolCall = new ChatModelToolCall
                {
                    Id = $"call_{Requests.Count}",
                    Name = "stub_tool",
                    ArgumentsJson = "{}",
                },
            };
            await Task.Yield();
        }
    }

    /// <summary>Minimal tool that always succeeds with an empty result.</summary>
    private sealed class StubTool : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "a stub";

        public string ParametersSchema => """{"type":"object"}""";

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult { Success = true, ResultJson = "{}" });
    }

    /// <summary>Hangs until cancelled — exercises the per-call timeout backstop.</summary>
    private sealed class HangingTool : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "hangs";

        public string ParametersSchema => """{"type":"object"}""";

        public async Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ToolResult { Success = true, ResultJson = "{}" };
        }
    }

    /// <summary>Plain text answer, capturing each request's <c>MaxTokens</c>.</summary>
    private sealed class RequestCapturingModel : IChatModelClient
    {
        public List<int?> MaxTokens { get; } = [];

        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            MaxTokens.Add(request.MaxTokens);
            yield return new ChatModelChunk { TextDelta = "ok" };
            await Task.Yield();
        }
    }

    /// <summary>
    /// Round 1: a text delta, then a tool-call NAME announce (live intent), then the
    /// completed call — exercising the early ToolCallStart path. Round 2: answers.
    /// </summary>
    private sealed class AnnouncingToolModel : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!request.Messages.Any(m => m.Role == "tool"))
            {
                yield return new ChatModelChunk { TextDelta = "working " };
                yield return new ChatModelChunk
                {
                    ToolCallStart = new ToolCallStart { Id = "call_x", Name = "stub_tool" },
                };
                yield return new ChatModelChunk
                {
                    ToolCall = new ChatModelToolCall { Id = "call_x", Name = "stub_tool", ArgumentsJson = "{}" },
                };
            }
            else
            {
                yield return new ChatModelChunk { TextDelta = "done" };
            }

            await Task.Yield();
        }
    }

    /// <summary>A stub tool that surfaces an artifact on its result (the make/edit path).</summary>
    private sealed class ArtifactStubTool(Artifact artifact) : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "makes an artifact";

        public string ParametersSchema => """{"type":"object"}""";

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult { Success = true, ResultJson = "{}", Artifacts = [artifact] });
    }

    /// <summary>Streams an ordinary (unnamed) code fence — must stay inline.</summary>
    private sealed class InlineCodeModel : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatModelChunk { TextDelta = "```python\nprint(1)\n```" };
            await Task.Yield();
        }
    }

    private sealed class ExplodingModel : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatModelChunk { TextDelta = "partial " };
            await Task.Yield();
            throw new InvalidOperationException("model exploded");
        }
    }

    private sealed class NeverFinishingModel : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatModelChunk { TextDelta = "thinking… " };
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
