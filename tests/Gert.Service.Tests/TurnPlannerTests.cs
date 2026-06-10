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
    private readonly IChatDatabaseProvider _provider = Substitute.For<IChatDatabaseProvider>();
    private readonly IRagDatabaseProvider _ragProvider = Substitute.For<IRagDatabaseProvider>();
    private readonly IValidationProvider _validation = Substitute.For<IValidationProvider>();
    private readonly TurnOptions _options = new();
    private readonly List<Message> _persisted = [];
    private readonly List<Message> _existing = [];
    private long _seq;

    public TurnPlannerTests()
    {
        _validation.Validate(Arg.Any<SendMessageRequest>()).Returns(ValidationResult.Success);

        _provider
            .OpenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            TimeProvider.System,
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
        await _provider.DidNotReceiveWithAnyArgs().OpenAsync(default!, default!, default!, default);
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
    public async Task Title_cap_cuts_on_a_grapheme_boundary_and_never_splits_a_surrogate_pair()
    {
        Conversation? inserted = null;
        await _repo.InsertConversationAsync(
            Arg.Do<Conversation>(c => inserted = c), Arg.Any<CancellationToken>());
        _repo.ClearReceivedCalls();

        // 59 ASCII chars, then an emoji (a surrogate PAIR straddling indexes 59–60):
        // a naive text[..60] would end on the lone high surrogate — invalid UTF-16.
        var content = new string('a', 59) + "\U0001F600 and more text well beyond the sixty-char cap";

        await NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = content });

        inserted.Should().NotBeNull();
        inserted!.Title.Length.Should().BeLessThanOrEqualTo(60);
        // The cut lands BEFORE the emoji (the whole grapheme is dropped, not torn).
        inserted.Title.Should().Be(new string('a', 59));
        inserted.Title.ToCharArray().Should().OnlyContain(c => !char.IsSurrogate(c), "no lone surrogate may survive the cut");
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
    public async Task PlannedAt_is_the_exact_instant_stamped_on_the_placeholder_row()
    {
        var job = await NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = "hello" });

        // One clock read, not two: the job's anchor IS the placeholder's
        // CreatedAt, so the runner's remaining-budget cap and the readers'
        // orphan/409 horizon measure from the identical instant
        // (chat-and-tools.md § detached turns — the shared-anchor invariant).
        var assistant = _persisted.Single(m => m.Role == MessageRole.Assistant);
        job.PlannedAt.Should().Be(assistant.CreatedAt);
        job.PlannedAt.Should().Be(_persisted.Single(m => m.Role == MessageRole.User).CreatedAt);
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
    public async Task Attachments_persist_on_the_user_row_and_ride_history_as_images()
    {
        SeedConversation();
        var now = DateTimeOffset.UtcNow;
        // A prior user turn with an image: its attachment must re-enter history
        // so the model keeps seeing earlier images.
        _existing.Add(new Message
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = Conv,
            Role = MessageRole.User,
            Content = "earlier image",
            Attachments = [new MessageAttachment { MimeType = "image/jpeg", Data = "b2xk" }],
            Status = MessageStatus.Complete,
            CreatedAt = now.AddMinutes(-1),
        });

        var request = new SendMessageRequest
        {
            Content = "what is this?",
            Attachments = [new MessageAttachment { MimeType = "image/png", Data = "aGVsbG8=" }],
        };

        // Default catalog (NullModelCatalog) is vision-permissive.
        var job = await NewPlanner().PlanAsync(Pid, Conv, request);

        var userRow = _persisted.Single(m => m.Role == MessageRole.User);
        userRow.Attachments.Should().ContainSingle()
            .Which.MimeType.Should().Be("image/png");

        var prior = job.History.Single(m => m.Content == "earlier image");
        prior.Images.Should().ContainSingle().Which.DataBase64.Should().Be("b2xk");

        var tail = job.History[^1];
        tail.Images.Should().ContainSingle();
        tail.Images![0].MimeType.Should().Be("image/png");
        tail.Images[0].DataBase64.Should().Be("aGVsbG8=");
    }

    [Fact]
    public async Task Images_are_dropped_from_history_for_a_catalog_gated_non_vision_model()
    {
        var catalog = Substitute.For<IModelCatalog>();
        catalog.SupportsTools(Arg.Any<string>()).Returns(true);
        catalog.SupportsVision(Arg.Any<string>()).Returns(false);

        var request = new SendMessageRequest
        {
            Content = "what is this?",
            Attachments = [new MessageAttachment { MimeType = "image/png", Data = "aGVsbG8=" }],
        };

        var job = await NewPlanner(catalog: catalog).PlanAsync(Pid, Conv, request);

        // The row keeps the attachment (UI truth) but the prompt degrades to
        // text-only rather than erroring the turn — mirror of the tools gate.
        _persisted.Single(m => m.Role == MessageRole.User).Attachments.Should().NotBeNull();
        job.History[^1].Images.Should().BeNull();
        job.History[^1].Content.Should().Be("what is this?");
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

    private void SeedTodoSnapshot(string responseJson) =>
        _repo.GetLatestToolCallAsync(Conv, "todo", Arg.Any<CancellationToken>())
            .Returns(new ToolCall
            {
                Id = Guid.NewGuid().ToString("D"),
                MessageId = Guid.NewGuid().ToString("D"),
                Kind = "todo",
                Status = ToolCallStatus.Done,
                ResponseJson = responseJson,
                CreatedAt = DateTimeOffset.UtcNow,
            });

    [Fact]
    public async Task Open_todo_snapshot_appends_the_reminder_to_the_rendered_user_message_only()
    {
        var user = new TestUserContext { AllowedTools = new HashSet<string>(["todo"], StringComparer.Ordinal) };
        SeedConversation(("todo", true));
        SeedExisting(MessageStatus.Complete, DateTimeOffset.UtcNow.AddMinutes(-1), content: "earlier turn");
        const string snapshot = """{"todos":[{"text":"step 2","status":"pending"}]}""";
        SeedTodoSnapshot(snapshot);

        var request = new SendMessageRequest
        {
            Content = "continue",
            Tools = new ToolToggles(new Dictionary<string, bool> { ["todo"] = true }),
        };

        var job = await NewPlanner(user, [new TodoTool()]).PlanAsync(Pid, Conv, request);

        // The reminder rides at the TAIL of the rendered prompt; prior history
        // keeps its exact bytes (prefix cache) and the persisted user row keeps
        // the user's actual words (UI truth).
        job.History[^1].Content.Should().Be("continue\n\n" + TodoTool.CrossTurnReminder(snapshot));
        job.History[0].Content.Should().Be("earlier turn");
        _persisted.Single(m => m.Role == MessageRole.User).Content.Should().Be("continue");
    }

    [Fact]
    public async Task Finished_todo_snapshot_appends_no_reminder()
    {
        var user = new TestUserContext { AllowedTools = new HashSet<string>(["todo"], StringComparer.Ordinal) };
        SeedConversation(("todo", true));
        SeedTodoSnapshot("""{"todos":[{"text":"step 1","status":"done"}]}""");

        var request = new SendMessageRequest
        {
            Content = "thanks!",
            Tools = new ToolToggles(new Dictionary<string, bool> { ["todo"] = true }),
        };

        var job = await NewPlanner(user, [new TodoTool()]).PlanAsync(Pid, Conv, request);

        // A finished list needs no revival — no prompt tokens spent on it.
        job.History[^1].Content.Should().Be("thanks!");
    }

    [Fact]
    public async Task No_reminder_when_the_todo_tool_is_not_offered()
    {
        // Snapshot exists, but the tool was toggled off for this turn — the
        // model could not update statuses, so the reminder would only mislead.
        var user = new TestUserContext { AllowedTools = new HashSet<string>(["todo"], StringComparer.Ordinal) };
        SeedConversation(("todo", false));
        SeedTodoSnapshot("""{"todos":[{"text":"step 2","status":"active"}]}""");

        var request = new SendMessageRequest
        {
            Content = "continue",
            Tools = new ToolToggles(new Dictionary<string, bool> { ["todo"] = true }),
        };

        var job = await NewPlanner(user, [new TodoTool()]).PlanAsync(Pid, Conv, request);

        job.History[^1].Content.Should().Be("continue");
    }

    [Fact]
    public async Task Broken_todo_snapshot_read_never_fails_the_turn()
    {
        var user = new TestUserContext { AllowedTools = new HashSet<string>(["todo"], StringComparer.Ordinal) };
        SeedConversation(("todo", true));
        _repo.GetLatestToolCallAsync(Conv, "todo", Arg.Any<CancellationToken>())
            .Returns<ToolCall?>(_ => throw new InvalidOperationException("disk on fire"));

        var request = new SendMessageRequest
        {
            Content = "continue",
            Tools = new ToolToggles(new Dictionary<string, bool> { ["todo"] = true }),
        };

        var job = await NewPlanner(user, [new TodoTool()]).PlanAsync(Pid, Conv, request);

        // Best-effort: the reminder is a nicety, never a turn-blocker.
        job.History[^1].Content.Should().Be("continue");
    }

    [Fact]
    public async Task Any_offered_ITailReminder_tool_is_revived_not_just_todo()
    {
        // The mechanism is generic: a non-todo tool that implements ITailReminder
        // gets its snapshot and its reminder appended at the tail, exactly like the
        // todo list — the planner has no per-tool branch.
        var user = new TestUserContext { AllowedTools = new HashSet<string>(["stub"], StringComparer.Ordinal) };
        SeedConversation(("stub", true));
        _repo.GetLatestToolCallAsync(Conv, "stub", Arg.Any<CancellationToken>())
            .Returns(new ToolCall
            {
                Id = Guid.NewGuid().ToString("D"),
                MessageId = Guid.NewGuid().ToString("D"),
                Kind = "stub",
                Status = ToolCallStatus.Done,
                ResponseJson = "STATE",
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var request = new SendMessageRequest
        {
            Content = "go",
            Tools = new ToolToggles(new Dictionary<string, bool> { ["stub"] = true }),
        };

        var job = await NewPlanner(user, [new StubRevivableTool()]).PlanAsync(Pid, Conv, request);

        job.History[^1].Content.Should().Be("go\n\n<revived>STATE</revived>");
    }

    /// <summary>
    /// A non-todo tool that opts into cross-turn revival — proof the reminder path
    /// keys off the <see cref="ITailReminder"/> interface, not a hard-coded tool id.
    /// Echoes its snapshot verbatim so the test can assert exact tail placement.
    /// </summary>
    private sealed class StubRevivableTool : ITool, ITailReminder
    {
        public string Id => "stub";

        public string Name => "stub";

        public string Description => string.Empty;

        public string ParametersSchema => "{}";

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult { Success = true });

        public string? BuildTailReminder(string? latestResultJson) =>
            string.IsNullOrEmpty(latestResultJson) ? null : $"<revived>{latestResultJson}</revived>";
    }

    /// <summary>A catalog declaring the Qwen3.6 instruct sampling for "default".</summary>
    private static IModelCatalog InstructCatalog()
    {
        var catalog = Substitute.For<IModelCatalog>();
        catalog.SupportsTools(Arg.Any<string>()).Returns(true);
        catalog.InstructParams("default").Returns(new GenerationParams
        {
            Temperature = 0.7,
            TopP = 0.8,
            PresencePenalty = 1.5,
        });
        return catalog;
    }

    [Fact]
    public async Task Thinking_off_applies_the_catalogs_instruct_sampling()
    {
        var job = await NewPlanner(catalog: InstructCatalog()).PlanAsync(
            Pid, Conv, new SendMessageRequest { Content = "hi", Thinking = false });

        // generation_config.json only ships the thinking-mode set, so a
        // thinking-off turn must carry the model's declared instruct sampling.
        job.Temperature.Should().Be(0.7);
        job.TopP.Should().Be(0.8);
        job.PresencePenalty.Should().Be(1.5);
    }

    [Fact]
    public async Task Thinking_on_or_default_leaves_sampling_to_the_model_defaults()
    {
        var jobDefault = await NewPlanner(catalog: InstructCatalog()).PlanAsync(
            Pid, Conv, new SendMessageRequest { Content = "hi" });
        var jobThinking = await NewPlanner(catalog: InstructCatalog()).PlanAsync(
            Pid, "conv-think", new SendMessageRequest { Content = "hi", Thinking = true });

        // Omitted fields let vLLM apply generation_config (the thinking set).
        jobDefault.Temperature.Should().BeNull();
        jobDefault.TopP.Should().BeNull();
        jobDefault.PresencePenalty.Should().BeNull();
        jobThinking.Temperature.Should().BeNull();
        jobThinking.PresencePenalty.Should().BeNull();
    }

    [Fact]
    public async Task Explicit_params_beat_the_instruct_fallback_field_by_field()
    {
        SeedConversation(new Conversation
        {
            Id = Conv,
            Title = "t",
            ModelId = "default",
            Params = new GenerationParams { Temperature = 0.3 },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var job = await NewPlanner(catalog: InstructCatalog()).PlanAsync(
            Pid, Conv, new SendMessageRequest { Content = "hi", Thinking = false });

        job.Temperature.Should().Be(0.3, "the conversation's explicit value wins");
        job.TopP.Should().Be(0.8, "unset fields still get the instruct fallback");
        job.PresencePenalty.Should().Be(1.5);
    }

    [Fact]
    public async Task Entitlement_ceiling_filters_unentitled_tools_and_snapshots_the_claim()
    {
        var user = new TestUserContext { AllowedTools = new HashSet<string>(["rag"], StringComparer.Ordinal) };
        var rag = new RagTool(_ragProvider, new FakeEmbeddings(), user);
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
        var rag = new RagTool(_ragProvider, new FakeEmbeddings(), user);
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
    public async Task Instructions_append_after_the_builtin_canvas_prompt()
    {
        var reader = Substitute.For<IProjectInstructionsReader>();
        reader.GetInstructionsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Always answer in haiku.");

        var job = await NewPlanner(instructions: reader)
            .PlanAsync(Pid, Conv, new SendMessageRequest { Content = "hello" });

        job.SystemPrompt.Should().StartWith(SystemPrompts.Canvas);
        job.SystemPrompt.Should().EndWith("Always answer in haiku.");
    }

    [Fact]
    public async Task The_canvas_convention_rides_every_turn_even_without_instructions()
    {
        // No instructions reader at all: real models still must learn to put
        // complete files in the canvas via the make_artifact tool.
        var job = await NewPlanner().PlanAsync(Pid, Conv, new SendMessageRequest { Content = "hello" });

        job.SystemPrompt.Should().Be(SystemPrompts.Canvas);
        job.SystemPrompt.Should().Contain("make_artifact");
    }
}
