using System.Collections.Generic;
using FluentAssertions;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Model.Rag;
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
/// Tool-loop tests for <see cref="ChatService"/> (U7b): the full
/// <c>tool_call → execute → tool_result → feed back → final text</c> sequence,
/// the persisted <c>tool_calls</c> row, and the entitlement ceiling enforced in
/// <b>both</b> phases — filtered out of the offered set in StartTurn, and refused
/// by <see cref="IUserContext.CanUseTool"/> at execute time.
/// </summary>
public sealed class ChatServiceToolLoopTests
{
    private readonly IChatRepository _repo = Substitute.For<IChatRepository>();
    private readonly IRagRepository _ragRepo = Substitute.For<IRagRepository>();
    private readonly IDatabaseProvider _provider = Substitute.For<IDatabaseProvider>();
    private readonly IValidationProvider _validation = Substitute.For<IValidationProvider>();
    private readonly List<Message> _persisted = new();

    public ChatServiceToolLoopTests()
    {
        _validation.Validate(Arg.Any<SendMessageRequest>()).Returns(ValidationResult.Success);

        _provider
            .OpenChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_repo);
        _provider
            .OpenRagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ragRepo);

        _repo.InsertMessageAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => _persisted.Add(ci.Arg<Message>()));
        _repo.ListMessagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<Message>)_persisted.ToArray());

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

    private static TestUserContext UserWith(params string[] tools) =>
        new() { AllowedTools = new HashSet<string>(tools, StringComparer.Ordinal) };

    [Fact]
    public async Task Rag_tool_loop_emits_full_event_sequence_and_feeds_result_back()
    {
        var user = UserWith("rag");
        // RagTool re-checks CanUseTool against ITS user; give it the same grant.
        var ragTool = new RagTool(_provider, new FakeEmbeddings(), user);
        var sut = new ChatService(_provider, new FakeChatModel(), user, _validation, new ITool[] { ragTool }, instructions: null);

        var request = RequestWith("search my docs about qdrant", ("rag", true));
        var events = await Run(sut, "default", "conv-1", request);

        var types = events.Select(e => e.GetType().Name).ToArray();

        // MessageStart → ToolCall(running) → ToolResult → Delta… → Citation → MessageEnd.
        types.First().Should().Be(nameof(MessageStartEvent));
        types.Last().Should().Be(nameof(MessageEndEvent));

        var toolCall = events.OfType<ToolCallEvent>().Single();
        toolCall.Kind.Should().Be("rag");
        toolCall.Status.Should().Be(ToolCallStatus.Running);

        var toolResult = events.OfType<ToolResultEvent>().Single();
        toolResult.Kind.Should().Be("rag");
        toolResult.Status.Should().Be(ToolCallStatus.Done);
        toolResult.Hits.Should().NotBeNullOrEmpty();

        // The ToolCall precedes the deltas (the result was fed back before the answer).
        Array.IndexOf(types, nameof(ToolResultEvent))
            .Should().BeLessThan(Array.IndexOf(types, nameof(DeltaEvent)));

        // Final text comes from after_tool.deltas.
        var text = string.Concat(events.OfType<DeltaEvent>().Select(d => d.Text));
        text.Should().Be("Based on your docs, sqlite-vec wins [1].");

        // A citation [1] was emitted (from the RAG hit).
        var citation = events.OfType<CitationEvent>().Single();
        citation.Ordinal.Should().Be(1);
        citation.Label.Should().Be("bench.pdf · p.1");

        // The tool_calls row was persisted with the kind + latency.
        await _repo.Received(1).InsertToolCallAsync(
            Arg.Is<ToolCall>(t => t.Kind == "rag" && t.Status == ToolCallStatus.Done && t.LatencyMs != null),
            Arg.Any<CancellationToken>());

        // The hybrid search actually ran (the tool result was produced, not stubbed).
        await _ragRepo.Received(1).HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<float>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Citation persisted to chat.db, bound to the assistant message id.
        var assistantId = events.OfType<MessageStartEvent>().Single().MessageId;
        await _repo.Received(1).InsertCitationsAsync(
            Arg.Is<IReadOnlyList<Citation>>(c => c.Count == 1 && c[0].MessageId == assistantId && c[0].Ordinal == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Entitlement_ceiling_filters_sandbox_in_StartTurn_even_when_requested_and_enabled()
    {
        // User is NOT granted sandbox (only rag).
        var user = UserWith("rag");
        var sandbox = new SandboxTool(new StubSandbox());
        var rag = new RagTool(_provider, new FakeEmbeddings(), user);
        var sut = new ChatService(_provider, new FakeChatModel(), user, _validation, new ITool[] { rag, sandbox }, instructions: null);

        // Request explicitly asks for sandbox (and rag), conversation enables both.
        var request = RequestWith("run python to add two and two", ("rag", true), ("sandbox", true));
        SeedConversation("conv-1", ("rag", true), ("sandbox", true));

        var turn = await sut.StartTurnAsync("default", "conv-1", request);

        // Phase 1: sandbox is filtered out of the offered set; rag survives.
        turn.ToolIds.Should().Contain("rag");
        turn.ToolIds.Should().NotContain("sandbox");
        turn.Tools.Select(t => t.Name).Should().NotContain("run_python");
        turn.Tools.Select(t => t.Name).Should().Contain("search_documents");
    }

    [Fact]
    public async Task Entitlement_ceiling_blocks_execution_in_RunAsync_if_a_disallowed_tool_is_called()
    {
        // The model scripts run_python, but the user is not entitled to sandbox.
        // Even if the spec somehow reached the model, CanUseTool refuses execution.
        var user = UserWith("rag"); // no sandbox
        var sandbox = new SandboxTool(new StubSandbox());

        // Force the offered set to (improperly) include the sandbox spec by building a
        // turn by hand — the second-line defence must still refuse to run it.
        var turn = new ChatTurn
        {
            Pid = "default",
            ConversationId = "conv-1",
            AssistantMessageId = Guid.NewGuid().ToString("D"),
            ModelId = "default",
            Messages = new[] { new ChatModelMessage { Role = "user", Content = "run python to add two and two" } },
            ToolIds = new[] { "sandbox" },
            Tools = new[]
            {
                new ChatToolSpec
                {
                    Name = sandbox.Name,
                    Description = sandbox.Description,
                    ParametersSchema = sandbox.ParametersSchema,
                },
            },
        };

        var sut = new ChatService(_provider, new FakeChatModel(), user, _validation, new ITool[] { sandbox }, instructions: null);

        var events = await Collect(sut.RunAsync(turn));

        // The tool was advertised + called, but execution was refused (Error status),
        // and the stub sandbox never actually ran (CanUseTool blocked it).
        var toolResult = events.OfType<ToolResultEvent>().Single();
        toolResult.Status.Should().Be(ToolCallStatus.Error);

        // Persisted as an errored tool_calls row.
        await _repo.Received(1).InsertToolCallAsync(
            Arg.Is<ToolCall>(t => t.Kind == "sandbox" && t.Status == ToolCallStatus.Error),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Instructions_are_prepended_as_a_system_message()
    {
        var user = UserWith("rag");
        var reader = Substitute.For<IProjectInstructionsReader>();
        reader.GetInstructionsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Always answer in haiku.");

        var sut = new ChatService(_provider, new FakeChatModel(), user, _validation, Array.Empty<ITool>(), reader);

        var turn = await sut.StartTurnAsync("default", "conv-1", new SendMessageRequest { Content = "hello" });

        turn.SystemPrompt.Should().Be("Always answer in haiku.");
    }

    // ---- helpers -----------------------------------------------------------

    private void SeedConversation(string id, params (string Id, bool On)[] toggles)
    {
        var map = toggles.ToDictionary(t => t.Id, t => t.On, StringComparer.Ordinal);
        _repo.GetConversationAsync(id, Arg.Any<CancellationToken>())
            .Returns(new Conversation
            {
                Id = id,
                Title = "t",
                ModelId = "default",
                Tools = new ToolToggles(map),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
    }

    private static SendMessageRequest RequestWith(string content, params (string Id, bool On)[] toggles)
    {
        var map = toggles.ToDictionary(t => t.Id, t => t.On, StringComparer.Ordinal);
        return new SendMessageRequest { Content = content, Tools = new ToolToggles(map) };
    }

    private async Task<IReadOnlyList<ChatEvent>> Run(
        ChatService sut, string pid, string conversationId, SendMessageRequest request)
    {
        // When the request carries toggles, also enable them on the conversation so the
        // conversation-enabled gate (a per-conversation preference) does not drop them.
        if (request.Tools is not null)
        {
            SeedConversation(conversationId, request.Tools.Toggles.Select(kv => (kv.Key, kv.Value)).ToArray());
        }

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
}
