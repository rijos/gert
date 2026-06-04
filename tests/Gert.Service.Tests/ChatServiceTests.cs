using System.Runtime.CompilerServices;
using FluentAssertions;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Service.Chat;
using Gert.Service.Database;
using Gert.Service.External;
using Gert.Service.Tools;
using Gert.Service.Validation;
using Gert.Testing.Fakes;
using NSubstitute;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// No-tool-path tests for the <see cref="ChatService"/> streaming orchestrator,
/// driven by the real <see cref="FakeChatModel"/> (shared fixtures) with an
/// NSubstitute <see cref="IChatRepository"/>/<see cref="IDatabaseProvider"/> and
/// a fixed <see cref="TestUserContext"/>.
/// </summary>
public sealed class ChatServiceTests
{
    private readonly IChatRepository _repo = Substitute.For<IChatRepository>();
    private readonly IDatabaseProvider _provider = Substitute.For<IDatabaseProvider>();
    private readonly TestUserContext _user = new();
    private readonly IValidationProvider _validation = Substitute.For<IValidationProvider>();

    public ChatServiceTests()
    {
        // The happy-path tests are about the orchestrator, not validation: stub the
        // provider to pass. The fail-closed provider itself is covered in the
        // Validation suite; a dedicated test below asserts ChatService throws when
        // the provider reports a failure.
        _validation.Validate(Arg.Any<SendMessageRequest>()).Returns(ValidationResult.Success);

        _provider
            .OpenChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_repo);

        // Mimic real persistence: inserted messages are visible to a later
        // ListMessagesAsync, so the model (FakeChatModel) sees the just-persisted
        // user turn and can resolve its fixture by the last user message.
        _repo.InsertMessageAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => _persisted.Add(ci.Arg<Message>()));
        _repo.ListMessagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<Message>)_persisted.ToArray());
    }

    private readonly List<Message> _persisted = new();

    private ChatService NewService(IChatModelClient model) =>
        new(_provider, model, _user, _validation, Array.Empty<ITool>(), instructions: null);

    [Fact]
    public async Task No_tool_path_emits_start_then_deltas_then_end()
    {
        // Exact fixture: deltas + completion_tokens = 14.
        var request = new SendMessageRequest { Content = "should I use Qdrant or sqlite-vec?" };
        var sut = NewService(new FakeChatModel());

        var events = await Run(sut, "default", "conv-1", request);

        events.Should().HaveCountGreaterThan(2);
        events.First().Should().BeOfType<MessageStartEvent>();
        events.Last().Should().BeOfType<MessageEndEvent>();

        // The middle is exactly the fixture's deltas, in order.
        var deltas = events.OfType<DeltaEvent>().Select(d => d.Text).ToArray();
        deltas.Should().Equal("Short version: ", "use ", "sqlite-vec ", "for a homelab at this scale.");

        // The concatenated deltas reconstruct the assistant content.
        string.Concat(deltas).Should().Be("Short version: use sqlite-vec for a homelab at this scale.");

        // Token count flows to message_end from the final chunk.
        events.OfType<MessageEndEvent>().Single().TokenCount.Should().Be(14);

        // No tool/citation/artifact events on the no-tool path.
        events.Any(e => e is ToolCallEvent or ToolResultEvent or CitationEvent or ArtifactEvent or ErrorEvent)
            .Should().BeFalse();
    }

    [Fact]
    public async Task Persists_user_then_assistant_messages()
    {
        var request = new SendMessageRequest { Content = "should I use Qdrant or sqlite-vec?" };
        var sut = NewService(new FakeChatModel());

        await Run(sut, "default", "conv-1", request);

        // User message persisted.
        await _repo.Received(1).InsertMessageAsync(
            Arg.Is<Message>(m =>
                m.Role == MessageRole.User &&
                m.ConversationId == "conv-1" &&
                m.Content == "should I use Qdrant or sqlite-vec?" &&
                m.ModelId == null),
            Arg.Any<CancellationToken>());

        // Assistant message persisted with the full content + token count.
        await _repo.Received(1).InsertMessageAsync(
            Arg.Is<Message>(m =>
                m.Role == MessageRole.Assistant &&
                m.ConversationId == "conv-1" &&
                m.Content == "Short version: use sqlite-vec for a homelab at this scale." &&
                m.TokenCount == 14),
            Arg.Any<CancellationToken>());

        await _repo.Received(2).InsertMessageAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Assistant_message_id_matches_message_start_event()
    {
        var request = new SendMessageRequest { Content = "hello" };
        var sut = NewService(new FakeChatModel());

        var events = await Run(sut, "default", "conv-1", request);
        var startId = events.OfType<MessageStartEvent>().Single().MessageId;

        await _repo.Received(1).InsertMessageAsync(
            Arg.Is<Message>(m => m.Role == MessageRole.Assistant && m.Id == startId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Opens_chat_db_for_the_user_context_identity_only()
    {
        var sut = NewService(new FakeChatModel());

        await Run(sut, "proj-9", "conv-1", new SendMessageRequest { Content = "hello" });

        // Both phases open chat.db for the same (iss, sub, pid) — start (persist user)
        // and run (persist assistant) — never an arbitrary/caller-supplied identity.
        await _provider.Received().OpenChatAsync(
            _user.Iss, _user.Sub, "proj-9", Arg.Any<CancellationToken>());
        await _provider.DidNotReceive().OpenChatAsync(
            Arg.Is<string>(s => s != _user.Iss),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Model_error_yields_error_event_after_start()
    {
        var sut = NewService(new ThrowingChatModel("upstream exploded"));

        var events = await Run(sut, "default", "conv-1", new SendMessageRequest { Content = "boom" });

        events.First().Should().BeOfType<MessageStartEvent>();
        events.OfType<ErrorEvent>().Single().Message.Should().Be("upstream exploded");
        events.Should().NotContain(e => e is MessageEndEvent);

        // The user message was persisted; the assistant message was NOT (faulted).
        await _repo.Received(1).InsertMessageAsync(
            Arg.Is<Message>(m => m.Role == MessageRole.User), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().InsertMessageAsync(
            Arg.Is<Message>(m => m.Role == MessageRole.Assistant), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_request_throws_validation_exception_before_any_disk_touch()
    {
        var failing = Substitute.For<IValidationProvider>();
        failing.Validate(Arg.Any<SendMessageRequest>()).Returns(
            ValidationResult.Failure(new[]
            {
                new ValidationError { Property = "Content", Message = "required" },
            }));

        var sut = new ChatService(
            _provider, new FakeChatModel(), _user, failing, Array.Empty<ITool>(), instructions: null);

        // Phase 1 throws ValidationException (the host maps it to a 400 ProblemDetails)
        // — it is no longer an in-stream ErrorEvent.
        var act = async () => await sut.StartTurnAsync(
            "default", "conv-1", new SendMessageRequest { Content = "" });

        var thrown = (await act.Should().ThrowAsync<ValidationException>()).Which;
        thrown.Result.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("Content");

        // Fail-closed: no chat.db opened and nothing persisted (before disk).
        await _provider.DidNotReceive().OpenChatAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().InsertMessageAsync(
            Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Drive both stateless phases (StartTurn → Run) and collect the events.</summary>
    private static async Task<IReadOnlyList<ChatEvent>> Run(
        ChatService sut, string pid, string conversationId, SendMessageRequest request)
    {
        var turn = await sut.StartTurnAsync(pid, conversationId, request);
        return await Collect(sut.RunAsync(turn));
    }

    private static async Task<IReadOnlyList<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> source)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in source)
        {
            list.Add(e);
        }

        return list;
    }

    /// <summary>
    /// An <see cref="IChatModelClient"/> that yields one partial chunk, then throws
    /// — a true mid-stream failure. The reachable <c>yield return</c> means no
    /// unreachable-code pragma is needed.
    /// </summary>
    private sealed class ThrowingChatModel : IChatModelClient
    {
        private readonly string _message;

        public ThrowingChatModel(string message) => _message = message;

        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatModelChunk { TextDelta = "partial" };
            throw new InvalidOperationException(_message);
        }
    }
}
