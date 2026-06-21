using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Gert.Agent;
using Gert.Agent.Hosting;
using Gert.Agent.Loop;
using Gert.Chat;
using Gert.Database;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Model.Rag;
using Gert.Rag;
using Gert.Service.Chat;
using Gert.Service.Chat.Bus;
using Gert.Testing;
using Gert.Testing.Fakes;
using Gert.Tools;
using Gert.Tools.Builtin;
using Gert.Tools.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Gert.Agent.Tests;

/// <summary>
/// The detached tool loop: event ORDER -- message_start -> tool_call(running)
/// -> tool_result(done) -> deltas -> citation -> message_end -- asserted
/// on the PUBLISHED <see cref="TurnEvent"/>s, plus the invariants: the
/// persist-before-publish protocol, live tool/citation persistence with
/// provenance, the entitlement SNAPSHOT ceiling, error rows persisting as
/// status=error, and the wall-clock cap.
/// </summary>
public sealed class TurnRunnerTests
{
    private const string Pid = "default";
    // A UUID: the runner now builds a ChatObjectResource per tool call, which
    // validates the conversation id's shape (StorageKeys.ValidateConversationId).
    private const string Conv = "11111111-1111-1111-1111-111111111111";
    private const string AssistantId = "assistant-msg-1";

    private readonly IChatRepository _repo = Substitute.For<IChatRepository>();
    private readonly IRagStore _ragRepo = Substitute.For<IRagStore>();
    private readonly IChatDatabaseProvider _chatProvider = Substitute.For<IChatDatabaseProvider>();
    private readonly IRagIndexProvider _ragProvider = Substitute.For<IRagIndexProvider>();
    private readonly IConversationBus _bus = Substitute.For<IConversationBus>();

    private readonly List<TurnEvent> _published = [];
    private readonly List<TurnEventRecord> _appended = [];
    private readonly List<string> _protocol = []; // ordered "append:N" / "publish:N" markers
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

    // The ask_user registry the runner wires into its ChatToolUi; the round-trip
    // test answers through this same instance.
    private readonly TurnQuestions _questions = new();

    private TurnRunner NewRunner(
        IChatModelClient model,
        IEnumerable<ITool>? tools = null,
        TurnOptions? options = null,
        TimeProvider? clock = null,
        ITurnCancellation? cancellation = null,
        ILogger<TurnRunner>? logger = null) =>
        new(_chatProvider, new FixedChatClientFactory(model), _bus,
            // The real loop, so the chat-shell tests still exercise it end to end.
            new AgentLoop(clock ?? TimeProvider.System, NullLogger<AgentLoop>.Instance),
            tools ?? [],
            // The runner now builds the project RAG resource + sub-agent delegate
            // itself; the RAG index provider + embedding client are its own deps.
            _ragProvider, new FakeEmbeddings(),
            Options.Create(options ?? new TurnOptions()),
            Options.Create(new ToolsOptions()),
            clock ?? TimeProvider.System,
            cancellation ?? new TurnCancellation(
                Options.Create(options ?? new TurnOptions()), clock ?? TimeProvider.System),
            _questions,
            logger ?? NullLogger<TurnRunner>.Instance);

    private static TurnJob NewJob(
        string userContent,
        IReadOnlyList<ITool>? offered = null,
        IReadOnlySet<string>? allowed = null,
        DateTimeOffset? plannedAt = null) => new()
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
        PlannedAt = plannedAt ?? DateTimeOffset.UtcNow,
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

        var final = _streamUpdates.Last();
        final.Status.Should().Be(MessageStatus.Complete);
        final.Content.Should().Be(string.Concat(Events.OfType<DeltaEvent>().Select(d => d.Text)));
    }

    [Fact]
    public async Task Every_published_event_is_persisted_first_with_the_same_seq()
    {
        await NewRunner(new FakeChatModel()).RunAsync(NewJob("hello"));

        _published.Should().NotBeEmpty();

        _appended.Select(a => a.Seq).Should().Equal(_published.Select(p => p.Seq));
        _appended.Select(a => a.Type)
            .Should().Equal(_published.Select(p => p.Event.Type.ToWireName()));

        // The protocol is strictly persist-before-publish (the splice's
        // gap-free guarantee depends on this order).
        foreach (var evt in _published)
        {
            _protocol.IndexOf($"append:{evt.Seq}")
                .Should().BeLessThan(
                    _protocol.IndexOf($"publish:{evt.Seq}"),
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
        // RagTool reaches the project RAG index through the host the runner builds
        // (over _ragProvider + the embedding client); it takes only validation now.
        var ragTool = new RagTool(Gert.Testing.Proof.Validation);

        await NewRunner(new FakeChatModel(), [ragTool])
            .RunAsync(NewJob("search my docs about qdrant", [ragTool]));

        var types = Events.Select(e => e.GetType().Name).ToArray();

        // Unchanged order contract: start -> tool events -> deltas -> citation -> end.
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
        citation.Label.Should().Be("bench.pdf - p.1");

        // The tool row persisted LIVE with kind + latency...
        var toolRow = _toolRows.Single();
        toolRow.Kind.Should().Be("rag");
        toolRow.Status.Should().Be(ToolCallStatus.Done);
        toolRow.LatencyMs.Should().NotBeNull();
        toolRow.MessageId.Should().Be(AssistantId);

        // ...and the citation row carries its provenance (the tree:
        // message -> tool_call -> citation) plus the message binding.
        _insertedCitations.Should().ContainSingle();
        _insertedCitations![0].MessageId.Should().Be(AssistantId);
        _insertedCitations[0].ToolCallId.Should().Be(toolRow.Id);
        _insertedCitations[0].Ordinal.Should().Be(1);
    }

    [Fact]
    public async Task Model_fault_finalizes_the_row_as_error_and_emits_a_terminal_error_event()
    {
        await NewRunner(new ExplodingModel()).RunAsync(NewJob("hello"));

        // The partial content survives on an error row, and the log ends in
        // `error` -- a generic one: exception detail never reaches the
        // user-visible event (style guide section 7; the detail-vs-log split
        // has its own test).
        Events.First().Should().BeOfType<MessageStartEvent>();
        Events.OfType<DeltaEvent>().Single().Text.Should().Be("partial ");
        Events.Last().Should().BeOfType<ErrorEvent>()
            .Which.Message.Should().NotContain("model exploded");
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
    public async Task A_job_already_past_its_plan_time_budget_is_cancelled_promptly_as_error()
    {
        // The shared-anchor invariant: the wall clock measures from PLAN time
        // (TurnJob.PlannedAt = the placeholder's CreatedAt), not run start. A
        // job that aged past MaxTurnDuration in the queue is already past the
        // reader-facing orphan horizon - the runner must not grant it a fresh
        // window during which readers report `error` while it streams.
        var clock = new ManualClock();
        var options = new TurnOptions { MaxTurnDuration = TimeSpan.FromMinutes(5) };
        var job = NewJob("hello", plannedAt: clock.GetUtcNow() - TimeSpan.FromMinutes(6));

        await NewRunner(new NeverFinishingModel(), options: options, clock: clock).RunAsync(job);

        Events.Last().Should().BeOfType<ErrorEvent>()
            .Which.Message.Should().Contain("exceeded");
        _streamUpdates.Last().Status.Should().Be(MessageStatus.Error);
    }

    [Fact]
    public async Task Queue_wait_counts_against_the_budget_so_the_cap_is_only_the_remainder()
    {
        // Most of the budget elapsed while the job waited in the queue: the
        // runner's effective cap is the slice that remains. The budget is
        // deliberately long - the turn ending within the elapsed bound below
        // proves the cap was the REMAINDER; a fresh full window would keep the
        // never-finishing model streaming for the whole 30 s.
        var clock = new ManualClock();
        var options = new TurnOptions { MaxTurnDuration = TimeSpan.FromSeconds(30) };
        var job = NewJob(
            "hello",
            plannedAt: clock.GetUtcNow() - (options.MaxTurnDuration - TimeSpan.FromMilliseconds(200)));

        var stopwatch = Stopwatch.StartNew();
        await NewRunner(new NeverFinishingModel(), options: options, clock: clock).RunAsync(job);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(10),
            "the effective cap must be the remaining ~200ms slice, never a fresh full budget");
        Events.Last().Should().BeOfType<ErrorEvent>()
            .Which.Message.Should().Contain("exceeded");
        _streamUpdates.Last().Status.Should().Be(MessageStatus.Error);
    }

    [Fact]
    public async Task Unexpected_fault_detail_goes_to_the_log_never_the_user_visible_event()
    {
        var logger = new CapturingLogger<TurnRunner>();

        await NewRunner(new ExplodingModel("secret detail"), logger: logger)
            .RunAsync(NewJob("hello"));

        // The persisted/published error event is generic - upstream exception
        // text can echo internal URLs or prompt fragments (style guide section 7)...
        var error = Events.Last().Should().BeOfType<ErrorEvent>().Subject;
        error.Message.Should().Be("Something went wrong running this turn.");
        _appended.Last().PayloadJson.Should().NotContain("secret detail");

        // ...and the detail lands on the log instead: error level, exception attached.
        var entry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        entry.Exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("secret detail");
    }

    [Fact]
    public async Task A_failed_finalise_never_throws_but_is_logged_at_warning()
    {
        // Every append fails (db on fire): the first emit faults the turn, the
        // best-effort error finalise fails too - still swallowed by design (the
        // orphan rule is the backstop) but no longer silently (style guide section 7).
        var logger = new CapturingLogger<TurnRunner>();
        _repo.AppendTurnEventAsync(Arg.Any<TurnEventRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("db on fire"));

        await NewRunner(new FakeChatModel(), logger: logger).RunAsync(NewJob("hello"));

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Exception != null);
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
        await turn; // a user cancel is a normal outcome - no throw

        // The row finalised cancelled with everything that streamed, and the log
        // ends in the terminal `cancelled` event - never an error.
        var final = _streamUpdates.Last();
        final.Status.Should().Be(MessageStatus.Cancelled);
        final.Content.Should().Be("thinking... ");
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
    public async Task A_plain_text_turn_with_code_fences_produces_no_artifact()
    {
        // Fenced code in the reply is just inline code - only the tools make artifacts.
        await NewRunner(new InlineCodeModel()).RunAsync(NewJob("show me some code"));

        Events.Should().NotContain(e => e is ArtifactEvent);
    }

    [Fact]
    public async Task A_tool_emitted_event_rides_the_persist_then_publish_protocol()
    {
        // The ToolInvocation.EmitAsync seam (ask_user's question_asked) routed
        // through the runner's emit: a tool-emitted event is durable before it is
        // live and lands between the call's tool_call and tool_result. The
        // loop-side seam behaviour is in AgentLoopTests; this asserts the
        // chat-shell's persist-then-publish ordering for it.
        var emitting = new MidExecutionEmittingTool();

        await NewRunner(new TextThenToolModel(), [emitting])
            .RunAsync(NewJob("ask away", [emitting]));

        var asked = Events.OfType<QuestionAskedEvent>().Single();
        asked.Id.Should().Be("call_1");
        asked.Questions.Single().Question.Should().Be("Which color?");

        var types = Events.Select(e => e.GetType().Name).ToList();
        types.IndexOf(nameof(QuestionAskedEvent))
            .Should().BeGreaterThan(types.IndexOf(nameof(ToolCallEvent)))
            .And.BeLessThan(types.IndexOf(nameof(ToolResultEvent)));

        // Same protocol assertions the runner's own events get.
        _appended.Select(a => a.Seq).Should().Equal(_published.Select(p => p.Seq));
        var seq = _published.Single(p => p.Event is QuestionAskedEvent).Seq;
        _protocol.IndexOf($"append:{seq}").Should().BeLessThan(_protocol.IndexOf($"publish:{seq}"));
    }

    [Fact]
    public async Task Ask_user_turn_round_trips_question_answer_and_final_reply()
    {
        // End to end over the shared fixture ("ask me which color"): the REAL
        // AskUserTool through the runner - it drives the runner's ChatToolUi,
        // so question_asked is emitted and durable, the registry answer resolves
        // the wait, question_answered + the after_tool reply land, and the
        // persisted row carries {answered, answer} for the thread GET rebuild.
        var tool = new AskUserTool();

        var turn = NewRunner(new FakeChatModel(), [tool])
            .RunAsync(NewJob("ask me which color", [tool]));

        // message_start -> tool_call(running) -> question_asked, then the runner
        // blocks on the wait - the published list is stable once it holds 3.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_published.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        var asked = Events.OfType<QuestionAskedEvent>().Single();
        asked.Questions.Single().Question.Should().Be("Which color?");
        asked.Questions.Single().Options.Should().Equal("red", "blue");

        _questions.Answer(
                new TurnKey("https://idp.example", "sub-123", Pid, Conv),
                Proof.Of(new AnswerRequest { QuestionId = asked.QuestionId, Answers = ["blue"] }))
            .Should().Be(AnswerOutcome.Delivered);

        await turn;

        Events.OfType<QuestionAnsweredEvent>().Single().Answers.Should().Equal("blue");
        string.Concat(Events.OfType<DeltaEvent>().Select(d => d.Text))
            .Should().Be("Noted - proceeding with your choice.");
        _finalized.Single().Status.Should().Be(MessageStatus.Complete);
        _toolRows.Single().ResponseJson.Should().Contain("\"answered\":true").And.Contain("blue");
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

        // Two rounds x 100ms of stream time; the 500ms tool execution between
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
            IToolHost host,
            CancellationToken cancellationToken = default)
        {
            clock.Advance(executionSpan);
            return Task.FromResult(new ToolResult { Success = true, ResultJson = "{}" });
        }
    }

    /// <summary>
    /// A TimeProvider whose timestamp and wall clock only move when the test
    /// advances them. The wall clock anchors at construction so a job whose
    /// <c>PlannedAt</c> the test does not back-date keeps its full budget.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public override long GetTimestamp() => _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by)
        {
            _timestamp += (long)(by.TotalSeconds * TimestampFrequency);
            _utcNow += by;
        }
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

    /// <summary>Minimal tool that always succeeds with an empty result.</summary>
    private sealed class StubTool : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "a stub";

        public string ParametersSchema => """{"type":"object"}""";

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult { Success = true, ResultJson = "{}" });
    }

    /// <summary>Emits a chat event mid-execution through the invocation's emit seam.</summary>
    private sealed class MidExecutionEmittingTool : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "emits mid-call";

        public string ParametersSchema => """{"type":"object"}""";

        public async Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default)
        {
            await invocation.EmitAsync!(
                new QuestionAskedEvent
                {
                    Id = invocation.ToolCallId!,
                    QuestionId = Guid.NewGuid().ToString("D"),
                    Questions = [new AskedQuestion("Which color?", null, ["red", "blue"], false)],
                },
                cancellationToken);
            return new ToolResult { Success = true, ResultJson = "{}" };
        }
    }

    /// <summary>Streams an ordinary (unnamed) code fence - must stay inline.</summary>
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

    private sealed class ExplodingModel(string message = "model exploded") : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatModelChunk { TextDelta = "partial " };
            await Task.Yield();
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Captures log entries in-memory so the fault-hygiene tests can assert on
    /// level and attached exception (no FakeLogger package in this suite).
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed class NeverFinishingModel : IChatModelClient
    {
        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatModelChunk { TextDelta = "thinking... " };
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
