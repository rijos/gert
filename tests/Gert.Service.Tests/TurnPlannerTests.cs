using FluentAssertions;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Service.Chat;
using Gert.Database;
using Gert.Service.External;
using Gert.Service.Tools;
using Gert.Service.Validation;
using Gert.Testing.Fakes;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// Phase 1 in the request scope (replaces the StartTurnAsync side of the
/// ChatService suites): validation fail-closed, conversation materialization,
/// the seq-stamped user row + streaming assistant placeholder, the
/// history/entitlement snapshot captured into the <see cref="TurnJob"/>, and
/// the new 409 rule with its orphan-rule escape hatch.
/// </summary>
public sealed class TurnPlannerTests
{
    private const string Pid = "default";
    private const string Conv = "conv-1";

    private readonly IChatRepository _repo = Substitute.For<IChatRepository>();
    private readonly IDatabaseProvider _provider = Substitute.For<IDatabaseProvider>();
    private readonly IValidationProvider _validation = Substitute.For<IValidationProvider>();
    private readonly TurnOptions _options = new();
    private readonly List<Message> _persisted = [];
    private readonly List<Message> _existing = [];
    private long _seq;

    public TurnPlannerTests()
    {
        _validation.Validate(Arg.Any<SendMessageRequest>()).Returns(ValidationResult.Success);

        _provider
            .OpenChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_repo);

        _repo.AllocateSeqAsync(Conv, Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref _seq));
        _repo.InsertMessageAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => _persisted.Add(ci.Arg<Message>()));
        _repo.ListMessagesAsync(Conv, Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<Message>)_existing.ToArray());
    }

    private TurnPlanner NewPlanner(
        TestUserContext? user = null,
        IEnumerable<ITool>? tools = null,
        IProjectInstructionsReader? instructions = null,
        IModelCatalog? catalog = null,
        Gert.Service.Projects.ISettingsService? settings = null) =>
        new(
            _provider,
            user ?? new TestUserContext(),
            _validation,
            tools ?? [],
            Options.Create(_options),
            instructions,
            catalog,
            settings);

    private void SeedConversation(params (string Id, bool On)[] toggles)
    {
        var map = toggles.ToDictionary(t => t.Id, t => t.On, StringComparer.Ordinal);
        SeedConversation(new Conversation
        {
            Id = Conv,
            Title = "t",
            ModelId = "default",
            Tools = new ToolToggles(map),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private void SeedConversation(Conversation conversation) =>
        _repo.GetConversationAsync(Conv, Arg.Any<CancellationToken>())
            .Returns(conversation);

    private void SeedExisting(
        MessageStatus status,
        DateTimeOffset createdAt,
        MessageRole role = MessageRole.Assistant,
        string content = "prior",
        string? reasoning = null) =>
        _existing.Add(new Message
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = Conv,
            Role = role,
            Content = content,
            Reasoning = reasoning,
            Status = status,
            CreatedAt = createdAt,
        });

    [Fact]
    public async Task Validation_failure_throws_before_any_disk_touch()
    {
        _validation.Validate(Arg.Any<SendMessageRequest>())
            .Returns(ValidationResult.Failure([new ValidationError { Property = "content", Message = "required" }]));

        var act = () => NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = "" });

        await act.Should().ThrowAsync<ValidationException>();
        await _provider.DidNotReceiveWithAnyArgs().OpenChatAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task New_conversation_is_materialised_with_a_derived_title()
    {
        var job = await NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = "hello world" });

        await _repo.Received(1).InsertConversationAsync(
            Arg.Is<Conversation>(c => c.Id == Conv && c.Title == "hello world"),
            Arg.Any<CancellationToken>());
        job.ConversationId.Should().Be(Conv);
    }

    [Fact]
    public async Task Persists_a_complete_user_row_and_a_streaming_assistant_placeholder_with_seqs()
    {
        var job = await NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = "hello" });

        _persisted.Should().HaveCount(2);

        var user = _persisted[0];
        user.Role.Should().Be(MessageRole.User);
        user.Content.Should().Be("hello");
        user.Status.Should().Be(MessageStatus.Complete);
        user.Seq.Should().Be(1);

        var assistant = _persisted[1];
        assistant.Role.Should().Be(MessageRole.Assistant);
        assistant.Content.Should().BeEmpty();
        assistant.Status.Should().Be(MessageStatus.Streaming);
        assistant.Seq.Should().Be(2);

        job.UserMessageId.Should().Be(user.Id);
        job.AssistantMessageId.Should().Be(assistant.Id);
        job.AssistantSeq.Should().Be(2);
    }

    [Fact]
    public async Task History_carries_prior_complete_turns_plus_the_new_user_message_only()
    {
        SeedConversation();
        var now = DateTimeOffset.UtcNow;
        SeedExisting(MessageStatus.Complete, now.AddMinutes(-10), MessageRole.User, "earlier question");
        SeedExisting(MessageStatus.Complete, now.AddMinutes(-9), MessageRole.Assistant, "earlier answer");
        // An old failed turn: its partial content must never re-enter the prompt.
        SeedExisting(MessageStatus.Error, now.AddMinutes(-5), MessageRole.Assistant, "partial garbage");
        // A user-stopped turn: same rule — the partial is UI context only.
        SeedExisting(MessageStatus.Cancelled, now.AddMinutes(-3), MessageRole.Assistant, "stopped partial");

        var job = await NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = "follow-up" });

        job.History.Select(m => m.Content)
            .Should().Equal("earlier question", "earlier answer", "follow-up");
        job.History.Last().Role.Should().Be("user");
    }

    [Fact]
    public async Task Thinking_request_overrides_conversation_and_persists_onto_it()
    {
        SeedConversation(new Conversation
        {
            Id = Conv,
            Title = "t",
            ModelId = "default",
            Thinking = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var job = await NewPlanner().PlanAsync(
            Pid, Conv, new SendMessageRequest { Content = "hi", Thinking = false });

        job.Thinking.Should().BeFalse("the request wins over the conversation");
        await _repo.Received(1).UpdateConversationAsync(
            Arg.Is<Conversation>(c => c.Thinking == false),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Thinking_falls_back_to_the_conversation_preference()
    {
        SeedConversation(new Conversation
        {
            Id = Conv,
            Title = "t",
            ModelId = "default",
            Thinking = false,
            PreserveThinking = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var job = await NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = "hi" });

        job.Thinking.Should().BeFalse();
        job.PreserveThinking.Should().BeTrue();
        // Nothing changed → no conversation write.
        await _repo.DidNotReceive().UpdateConversationAsync(
            Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preserve_thinking_carries_prior_assistant_reasoning_into_history()
    {
        SeedConversation(new Conversation
        {
            Id = Conv,
            Title = "t",
            ModelId = "default",
            PreserveThinking = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var now = DateTimeOffset.UtcNow;
        SeedExisting(MessageStatus.Complete, now.AddMinutes(-2), MessageRole.User, "q");
        SeedExisting(MessageStatus.Complete, now.AddMinutes(-1), MessageRole.Assistant, "391", reasoning: "17*23 = 391.");

        var job = await NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = "next" });

        var assistant = job.History.Single(m => m.Role == "assistant");
        assistant.ReasoningContent.Should().Be("17*23 = 391.");
        job.History.Single(m => m.Content == "q").ReasoningContent.Should().BeNull();
    }

    [Fact]
    public async Task Reasoning_stays_out_of_history_when_preserve_thinking_is_off()
    {
        SeedConversation();
        var now = DateTimeOffset.UtcNow;
        SeedExisting(MessageStatus.Complete, now.AddMinutes(-1), MessageRole.Assistant, "391", reasoning: "17*23 = 391.");

        var job = await NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = "next" });

        job.History.Single(m => m.Role == "assistant").ReasoningContent.Should().BeNull();
    }

    [Fact]
    public async Task Per_model_user_params_fill_fields_the_conversation_leaves_unset()
    {
        SeedConversation(new Conversation
        {
            Id = Conv,
            Title = "t",
            ModelId = "default",
            Params = new GenerationParams { Temperature = 0.7 },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var settings = Substitute.For<Gert.Service.Projects.ISettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new Gert.Model.Projects.UserSettings
        {
            ModelParams = new Dictionary<string, GenerationParams>(StringComparer.Ordinal)
            {
                ["default"] = new GenerationParams { Temperature = 0.3, TopP = 0.9, MaxTokens = 2048 },
            },
        });

        var job = await NewPlanner(settings: settings).PlanAsync(Pid, Conv, new SendMessageRequest { Content = "hi" });

        // Conversation wins field-by-field; settings fill the gaps.
        job.Temperature.Should().Be(0.7);
        job.TopP.Should().Be(0.9);
        job.MaxTokens.Should().Be(2048);
    }

    [Fact]
    public async Task A_broken_settings_read_never_fails_the_turn()
    {
        SeedConversation();
        var settings = Substitute.For<Gert.Service.Projects.ISettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>())
            .Returns<Gert.Model.Projects.UserSettings>(_ => throw new InvalidOperationException("boom"));

        var job = await NewPlanner(settings: settings).PlanAsync(Pid, Conv, new SendMessageRequest { Content = "hi" });

        job.Temperature.Should().BeNull();
        job.AssistantMessageId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_cancelled_turn_does_not_block_new_turns()
    {
        SeedConversation();
        // Just stopped seconds ago: cancelled is terminal, never in-progress.
        SeedExisting(MessageStatus.Cancelled, DateTimeOffset.UtcNow.AddSeconds(-2));

        var job = await NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = "again" });

        job.AssistantMessageId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Second_turn_while_one_is_streaming_throws_TurnInProgress()
    {
        SeedConversation();
        SeedExisting(MessageStatus.Streaming, DateTimeOffset.UtcNow.AddSeconds(-5));

        var act = () => NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = "again" });

        await act.Should().ThrowAsync<TurnInProgressException>();
        _persisted.Should().BeEmpty("the 409 must reject before any write");
    }

    [Fact]
    public async Task An_orphaned_streaming_row_does_not_block_new_turns()
    {
        SeedConversation();
        // Older than MaxTurnDuration: the worker that owned it is gone (crash /
        // lost queue) — the orphan rule treats it as error, so new turns proceed.
        SeedExisting(MessageStatus.Streaming, DateTimeOffset.UtcNow - _options.MaxTurnDuration - TimeSpan.FromMinutes(1));

        var job = await NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = "retry" });

        job.AssistantMessageId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Entitlement_ceiling_filters_unentitled_tools_and_snapshots_the_claim()
    {
        var user = new TestUserContext { AllowedTools = new HashSet<string>(["rag"], StringComparer.Ordinal) };
        var rag = new RagTool(_provider, new FakeEmbeddings(), user);
        var sandbox = new SandboxTool(new StubSandbox());
        SeedConversation(("rag", true), ("sandbox", true));

        var request = new SendMessageRequest
        {
            Content = "run python to add two and two",
            Tools = new ToolToggles(new Dictionary<string, bool> { ["rag"] = true, ["sandbox"] = true }),
        };

        var job = await NewPlanner(user, [rag, sandbox]).PlanAsync(Pid, Conv, request);

        // Offered = requested ∩ enabled ∩ entitlement ∩ registry.
        job.ToolIds.Should().Contain("rag").And.NotContain("sandbox");
        job.Tools.Select(t => t.Name).Should().Contain("search_documents").And.NotContain("run_python");

        // The snapshot is the claim, captured for the off-thread re-check.
        job.AllowedToolIds.Should().BeEquivalentTo(["rag"]);
        job.Iss.Should().Be(user.Iss);
        job.Sub.Should().Be(user.Sub);
    }

    [Fact]
    public async Task Model_without_tool_capability_is_offered_no_tools()
    {
        var user = new TestUserContext { AllowedTools = new HashSet<string>(["rag"], StringComparer.Ordinal) };
        var rag = new RagTool(_provider, new FakeEmbeddings(), user);
        SeedConversation(("rag", true));

        var catalog = Substitute.For<IModelCatalog>();
        catalog.SupportsTools("default").Returns(false);

        var request = new SendMessageRequest
        {
            Content = "search my documents",
            Tools = new ToolToggles(new Dictionary<string, bool> { ["rag"] = true }),
        };

        var job = await NewPlanner(user, [rag], catalog: catalog).PlanAsync(Pid, Conv, request);

        // Requested + enabled + entitled — but the model can't call tools, so
        // nothing is advertised upstream.
        job.ToolIds.Should().BeEmpty();
        job.Tools.Should().BeEmpty();
    }

    [Fact]
    public async Task Instructions_resolve_into_the_system_prompt()
    {
        var reader = Substitute.For<IProjectInstructionsReader>();
        reader.GetInstructionsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Always answer in haiku.");

        var job = await NewPlanner(instructions: reader)
            .PlanAsync(Pid, Conv, new SendMessageRequest { Content = "hello" });

        job.SystemPrompt.Should().Be("Always answer in haiku.");
    }
}
