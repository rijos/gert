using System.Runtime.CompilerServices;
using FluentAssertions;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Service.Chat;
using Gert.Service.Database;
using Gert.Service.External;
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
    private readonly IValidationProvider _validation = new PassthroughValidationProvider(
        Substitute.For<IServiceProvider>());

    public ChatServiceTests()
    {
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
        new(_provider, model, _user, _validation);

    [Fact]
    public async Task No_tool_path_emits_start_then_deltas_then_end()
    {
        // Exact fixture: deltas + completion_tokens = 14.
        var request = new SendMessageRequest { Content = "should I use Qdrant or sqlite-vec?" };
        var sut = NewService(new FakeChatModel());

        var events = await Collect(sut.SendMessageAsync("default", "conv-1", request));

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

        await Collect(sut.SendMessageAsync("default", "conv-1", request));

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

        var events = await Collect(sut.SendMessageAsync("default", "conv-1", request));
        var startId = events.OfType<MessageStartEvent>().Single().MessageId;

        await _repo.Received(1).InsertMessageAsync(
            Arg.Is<Message>(m => m.Role == MessageRole.Assistant && m.Id == startId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Opens_chat_db_for_the_user_context_identity_only()
    {
        var sut = NewService(new FakeChatModel());

        await Collect(sut.SendMessageAsync(
            "proj-9", "conv-1", new SendMessageRequest { Content = "hello" }));

        await _provider.Received(1).OpenChatAsync(
            _user.Iss, _user.Sub, "proj-9", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Model_error_yields_error_event_after_start()
    {
        var sut = NewService(new ThrowingChatModel("upstream exploded"));

        var events = await Collect(sut.SendMessageAsync(
            "default", "conv-1", new SendMessageRequest { Content = "boom" }));

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
    public async Task Invalid_request_yields_only_an_error_event()
    {
        var failing = Substitute.For<IValidationProvider>();
        failing.Validate(Arg.Any<SendMessageRequest>()).Returns(
            ValidationResult.Failure(new[]
            {
                new ValidationError { Property = "Content", Message = "required" },
            }));

        var sut = new ChatService(_provider, new FakeChatModel(), _user, failing);

        var events = await Collect(sut.SendMessageAsync(
            "default", "conv-1", new SendMessageRequest { Content = "" }));

        events.Should().ContainSingle().Which.Should().BeOfType<ErrorEvent>();
        // Fail-closed before any disk/model touch.
        await _provider.DidNotReceive().OpenChatAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().InsertMessageAsync(
            Arg.Any<Message>(), Arg.Any<CancellationToken>());
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

    /// <summary>An <see cref="IChatModelClient"/> that throws mid-stream.</summary>
    private sealed class ThrowingChatModel : IChatModelClient
    {
        private readonly string _message;

        public ThrowingChatModel(string message) => _message = message;

        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException(_message);
#pragma warning disable CS0162 // Unreachable — satisfies the iterator contract.
            yield break;
#pragma warning restore CS0162
        }
    }
}
