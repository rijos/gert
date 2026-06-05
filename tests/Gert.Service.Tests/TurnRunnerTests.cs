using System.Runtime.CompilerServices;
using FluentAssertions;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Model.Rag;
using Gert.Service.Chat;
using Gert.Service.Chat.Bus;
using Gert.Service.Database;
using Gert.Service.External;
using Gert.Service.Tools;
using Gert.Testing.Fakes;
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
    private readonly IDatabaseProvider _provider = Substitute.For<IDatabaseProvider>();
    private readonly IConversationBus _bus = Substitute.For<IConversationBus>();

    private readonly List<TurnEvent> _published = [];
    private readonly List<TurnEventRecord> _appended = [];
    private readonly List<string> _protocol = []; // "append:N" / "publish:N"
    private readonly List<ToolCall> _toolRows = [];
    private readonly List<(string Content, MessageStatus Status, int? TokenCount)> _streamUpdates = [];
    private IReadOnlyList<Citation>? _insertedCitations;
    private long _seq = 2; // plan time allocated 1 (user) and 2 (assistant)

    public TurnRunnerTests()
    {
        _provider
            .OpenChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_repo);
        _provider
            .OpenRagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        TurnOptions? options = null) =>
        new(_provider, model, _bus, tools ?? [], Options.Create(options ?? new TurnOptions()));

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
        var ragTool = new RagTool(_provider, new FakeEmbeddings(), user);

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

    // ---- scripted models -----------------------------------------------------

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
