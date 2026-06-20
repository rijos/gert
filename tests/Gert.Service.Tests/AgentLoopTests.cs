using System.Runtime.CompilerServices;
using FluentAssertions;
using Gert.Chat;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Events;
using Gert.Service.Chat;
using Gert.Testing.Fakes;
using Gert.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// The reusable tool loop driven DIRECTLY (no chat shell, no repo, no bus): a
/// FakeToolHost + in-memory Emit/OnToolExecuted captures stand in for the driver's
/// callbacks. These are the loop's own invariants - tool rounds and the OpenAI
/// wire shape, the round + search budgets, the per-call entitlement re-check and
/// unentitled-call invisibility, the Modal-timeout exemption + generic backstop,
/// the mid-execution emit seam, the artifact rideback, and MaxTokensPerRound.
/// The persist-then-publish ordering, citation renumber/persist, and the terminal
/// finalize are the driver's job and stay in TurnRunnerTests.
/// </summary>
public sealed class AgentLoopTests
{
    private const string Pid = "default";
    private const string Conv = "11111111-1111-1111-1111-111111111111";
    private const string AssistantId = "assistant-msg-1";

    private readonly List<ChatEvent> _emitted = [];
    private readonly List<ExecutedToolCall> _executed = [];
    private readonly List<string> _progress = [];

    private IReadOnlyList<ChatEvent> Events => _emitted;

    private Task Emit(ChatEvent ev, CancellationToken token)
    {
        _emitted.Add(ev);
        return Task.CompletedTask;
    }

    private Task OnToolExecuted(ExecutedToolCall executed, CancellationToken token)
    {
        _executed.Add(executed);
        return Task.CompletedTask;
    }

    private Task OnProgress(string streamed, CancellationToken token)
    {
        _progress.Add(streamed);
        return Task.CompletedTask;
    }

    private static AgentLoop NewLoop(TimeProvider? clock = null) =>
        new(clock ?? TimeProvider.System, NullLogger<AgentLoop>.Instance);

    private AgentLoopRequest Request(
        IChatModelClient model,
        IReadOnlyList<ITool>? tools = null,
        IReadOnlySet<string>? allowed = null,
        IToolHost? host = null,
        TurnOptions? options = null,
        string userContent = "hello")
    {
        options ??= new TurnOptions();
        tools ??= [];
        return new AgentLoopRequest
        {
            Messages = [new ChatModelMessage { Role = "user", Content = userContent }],
            ToolSpecs = tools.Select(t => new ChatToolSpec
            {
                Name = t.Name,
                Description = t.Description,
                ParametersSchema = t.ParametersSchema,
            }).ToList(),
            Tools = tools,
            ModelId = "default",
            Model = model,
            Host = host ?? new FakeToolHost(),
            Pid = Pid,
            ConversationId = Conv,
            MessageId = AssistantId,
            AllowedToolIds = allowed ?? tools.Select(t => t.Id).ToHashSet(StringComparer.Ordinal),
            MaxRounds = options.MaxToolRounds,
            MaxTokensPerRound = options.MaxTokensPerRound,
            MaxSearchCallsPerTurn = options.MaxSearchCallsPerTurn,
            ToolCallTimeout = options.ToolCallTimeout,
            DeltaFlushInterval = options.DeltaFlushInterval,
            DeltaFlushMaxChars = options.DeltaFlushMaxChars,
            Emit = Emit,
            OnToolExecuted = OnToolExecuted,
            OnProgress = OnProgress,
        };
    }

    [Fact]
    public async Task Tool_round_history_carries_one_assistant_message_with_all_tool_calls()
    {
        var stub = new StubTool();
        var model = new TwoCallToolModel();

        await NewLoop().RunAsync(Request(model, [stub]));

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

        await NewLoop().RunAsync(Request(model, [stub]));

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
    public async Task A_streamed_tool_name_announces_a_running_card_before_the_call_completes()
    {
        var stub = new StubTool();
        await NewLoop().RunAsync(Request(new AnnouncingToolModel(), [stub], userContent: "go"));

        // The live-intent announce (name only, mid-stream) and the end-of-round
        // running event (now with args) share the call id - two Running tool_call
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
    public async Task Entitlement_snapshot_blocks_execution_silently_even_when_the_spec_reached_the_model()
    {
        // The stub spec is (improperly) offered, but the entitlement snapshot does
        // not include it - the off-thread second-line defence must refuse. The
        // refusal is invisible: NO card, NO OnToolExecuted persistence; only a
        // synthetic refusal is fed upstream, which the model reads and answers around.
        var stub = new StubTool();

        await NewLoop().RunAsync(Request(
            new AnnouncingToolModel(),
            [stub],
            allowed: new HashSet<string>(["rag"], StringComparer.Ordinal),
            userContent: "go"));

        // Not a flicker of activity for the refused call - no running announce, no
        // result card, no persisted row.
        Events.OfType<ToolCallEvent>().Should().BeEmpty();
        Events.OfType<ToolResultEvent>().Should().BeEmpty();
        _executed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tool_that_returns_an_artifact_emits_it_after_the_tool_result()
    {
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
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var tool = new ArtifactStubTool(artifact);

        await NewLoop().RunAsync(Request(new TextThenToolModel(), [tool]));

        var artifactEvent = Events.OfType<ArtifactEvent>().Single();
        artifactEvent.Id.Should().Be("art-9");
        artifactEvent.Kind.Should().Be(ArtifactKind.Html);
        artifactEvent.Name.Should().Be("demo.html");
        artifactEvent.Content.Should().Be("<h1>Demo</h1>");

        // Emitted right after the tool_result that produced it.
        var types = Events.Select(e => e.GetType().Name).ToList();
        types.IndexOf(nameof(ArtifactEvent))
            .Should().BeGreaterThan(types.IndexOf(nameof(ToolResultEvent)));

        // The executed call carries the artifact back to the driver.
        _executed.Single().Artifacts.Should().ContainSingle().Which.Id.Should().Be("art-9");
    }

    [Fact]
    public async Task Runaway_tool_loop_is_bounded_and_returns_with_the_streamed_content()
    {
        var stub = new StubTool();
        var model = new AlwaysToolCallingModel();

        // A model that requests a tool on EVERY round. Past the cap the loop
        // refuses the calls, winds down once with no tools advertised, and when
        // even that round emits tool calls it stops calling upstream.
        var result = await NewLoop().RunAsync(
            Request(model, [stub], options: new TurnOptions { MaxToolRounds = 5 }));

        // 5 executed rounds + 1 refused round + 1 wind-down round = 7 upstream
        // calls, hard stop.
        model.Tools.Should().HaveCount(7);
        model.Tools.Take(6).Should().OnlyContain(
            t => t.Count == 1,
            "executed and refused rounds still advertise the tool");
        model.Tools[6].Should().BeEmpty("the wind-down round must not advertise tools");

        result.Content.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Capped_round_refuses_calls_with_synthetic_results_keeping_the_wire_format()
    {
        var stub = new StubTool();
        var model = new AlwaysToolCallingModel();

        await NewLoop().RunAsync(Request(model, [stub], options: new TurnOptions { MaxToolRounds = 5 }));

        // The refused round still answers each call in the upstream history -
        // ONE assistant message carrying the round's narration + tool calls,
        // then a budget-exhausted tool result per call.
        var windDown = model.Requests[6];
        var assistant = windDown.Last(m => m.Role == "assistant");
        assistant.ToolCalls.Should().ContainSingle();
        assistant.Content.Should().Contain("round 6");

        var toolMsg = windDown.Last(m => m.Role == "tool");
        toolMsg.ToolCallId.Should().Be(assistant.ToolCalls![0].Id);
        toolMsg.Content.Should().Contain("tool budget exhausted");

        // Five executed-done calls; the refused round's call is an error.
        _executed.Count(r => r.Status == ToolCallStatus.Done).Should().Be(5);
        _executed.Count(r => r.Status == ToolCallStatus.Error).Should().Be(1);

        // The cap-trip is VISIBLE: the refused call's result event carries the
        // budget message for the card's error line.
        var refused = Events.OfType<ToolResultEvent>().Single(e => e.Status == ToolCallStatus.Error);
        refused.Error.Should().Contain("tool budget exhausted");
    }

    [Fact]
    public async Task Per_turn_search_budget_refuses_searches_past_the_cap()
    {
        var search = new CountingSearchTool();
        var model = new SearchingModel(rounds: 4);

        await NewLoop().RunAsync(
            Request(model, [search], options: new TurnOptions { MaxSearchCallsPerTurn = 2 }));

        // Two searches execute, the rest refuse with a synthetic budget result.
        search.Executions.Should().Be(2);
        _executed.Count(r => r.Status == ToolCallStatus.Done).Should().Be(2);
        _executed.Count(r => r.Status == ToolCallStatus.Error).Should().Be(2);

        var refused = Events.OfType<ToolResultEvent>()
            .Where(e => e.Status == ToolCallStatus.Error).ToList();
        refused.Should().HaveCount(2);
        refused.Should().OnlyContain(e => e.Error!.Contains("web search budget exhausted"));
    }

    [Fact]
    public async Task Disabled_search_budget_lets_every_search_run()
    {
        var search = new CountingSearchTool();
        var model = new SearchingModel(rounds: 4);

        await NewLoop().RunAsync(
            Request(model, [search], options: new TurnOptions { MaxSearchCallsPerTurn = 0 }));

        search.Executions.Should().Be(4);
        _executed.Should().OnlyContain(r => r.Status == ToolCallStatus.Done);
    }

    [Fact]
    public async Task Per_round_max_tokens_is_the_configured_bound_else_unset()
    {
        // Every round carries the configured MaxTokensPerRound, or nothing when it
        // is disabled (0), leaving the provider's own default to apply.
        var model = new RequestCapturingModel();

        await NewLoop().RunAsync(Request(model, options: new TurnOptions { MaxTokensPerRound = 100 }));
        await NewLoop().RunAsync(Request(model, options: new TurnOptions { MaxTokensPerRound = 0 }));

        model.MaxTokens.Should().Equal(new int?[] { 100, null });
    }

    [Fact]
    public async Task Hung_tool_call_times_out_with_a_visible_error_and_the_loop_completes()
    {
        var hung = new HangingTool();
        var options = new TurnOptions { ToolCallTimeout = TimeSpan.FromMilliseconds(50) };

        await NewLoop().RunAsync(Request(new TextThenToolModel(), [hung], options: options));

        // The call failed visibly - card error text names the timeout - and the
        // loop went on to a final answer instead of dying with it.
        var result = Events.OfType<ToolResultEvent>().Single();
        result.Status.Should().Be(ToolCallStatus.Error);
        result.Error.Should().Contain("timed out");
        _executed.Single().Status.Should().Be(ToolCallStatus.Error);
    }

    [Fact]
    public async Task An_interactive_tool_is_exempt_from_the_generic_tool_call_timeout()
    {
        // A Modal tool must outlive ToolCallTimeout (blocking IS its job): the loop
        // skips the per-call backstop for modal tools.
        var slow = new SlowInteractiveTool(TimeSpan.FromMilliseconds(300));
        var options = new TurnOptions { ToolCallTimeout = TimeSpan.FromMilliseconds(50) };

        await NewLoop().RunAsync(Request(new TextThenToolModel(), [slow], options: options));

        var result = Events.OfType<ToolResultEvent>().Single();
        result.Status.Should().Be(ToolCallStatus.Done);
        _executed.Single().Status.Should().Be(ToolCallStatus.Done);
    }

    [Fact]
    public async Task A_tool_emitted_event_rides_the_invocation_emit_seam()
    {
        // The ToolInvocation.EmitAsync seam (ask_user's question_asked): the loop
        // hands tools its OWN emit, so a mid-execution event lands between the
        // call's tool_call and tool_result like any other event.
        var emitting = new MidExecutionEmittingTool();

        await NewLoop().RunAsync(Request(new TextThenToolModel(), [emitting]));

        var asked = Events.OfType<QuestionAskedEvent>().Single();
        asked.Id.Should().Be("call_1");
        asked.Questions.Single().Question.Should().Be("Which color?");

        var types = Events.Select(e => e.GetType().Name).ToList();
        types.IndexOf(nameof(QuestionAskedEvent))
            .Should().BeGreaterThan(types.IndexOf(nameof(ToolCallEvent)))
            .And.BeLessThan(types.IndexOf(nameof(ToolResultEvent)));

        // The invocation carried the turn's deadline (here null) for the tool's budget math.
        emitting.DeadlineSeen.Should().BeTrue();
    }

    [Fact]
    public async Task An_autonomous_run_with_no_emit_persists_nothing_and_streams_no_events()
    {
        // An autonomous driver (sub-agent / headless) passes null callbacks: the
        // loop emits nothing, persists nothing, and still returns the final answer.
        var stub = new StubTool();
        var request = Request(new TextThenToolModel(), [stub]) with
        {
            Emit = null,
            OnToolExecuted = null,
            OnProgress = null,
        };

        var result = await NewLoop().RunAsync(request);

        _emitted.Should().BeEmpty();
        _executed.Should().BeEmpty();
        _progress.Should().BeEmpty();
        result.Content.Should().Be("checking done");
    }

    // ----- fakes (loop-local; the chat-shell fakes live in TurnRunnerTests) -----

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
    /// Requests a tool on EVERY round - the runaway tool loop. Captures each
    /// request's messages and advertised tools.
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

    /// <summary>
    /// Calls <c>web_search</c> for the first N rounds, then answers plain text.
    /// </summary>
    private sealed class SearchingModel(int rounds) : IChatModelClient
    {
        private int _round;

        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _round++;
            if (_round <= rounds)
            {
                yield return new ChatModelChunk
                {
                    ToolCall = new ChatModelToolCall
                    {
                        Id = $"call_{_round}",
                        Name = "web_search",
                        ArgumentsJson = """{"query":"x"}""",
                    },
                };
            }
            else
            {
                yield return new ChatModelChunk { TextDelta = "done" };
            }

            await Task.Yield();
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
    /// Round 1: a text delta, a tool-call NAME announce (live intent), then the
    /// completed call - exercising the early ToolCallStart path. Round 2: answers.
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

    /// <summary>A "search"-id tool that counts how often it actually executes.</summary>
    private sealed class CountingSearchTool : ITool
    {
        private int _executions;

        public int Executions => _executions;

        public string Id => "search";

        public string Name => "web_search";

        public string Description => "a counting search";

        public string ParametersSchema => """{"type":"object"}""";

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executions);
            return Task.FromResult(new ToolResult { Success = true, ResultJson = "{}" });
        }
    }

    /// <summary>A Modal tool that takes longer than the generic per-call timeout.</summary>
    private sealed class SlowInteractiveTool(TimeSpan wait) : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "waits on the user";

        public string ParametersSchema => """{"type":"object"}""";

        public ToolType Type => ToolType.Modal;

        public async Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(wait, cancellationToken);
            return new ToolResult { Success = true, ResultJson = "{}" };
        }
    }

    /// <summary>Emits a chat event mid-execution through the invocation's emit seam.</summary>
    private sealed class MidExecutionEmittingTool : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "emits mid-call";

        public string ParametersSchema => """{"type":"object"}""";

        public bool DeadlineSeen { get; private set; }

        public async Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default)
        {
            // The host's deadline is surfaced on the invocation (null here; the
            // chat shell wires the real turn deadline - tested in TurnRunnerTests).
            DeadlineSeen = invocation.Deadline == host.Limits.Deadline;
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

    /// <summary>Hangs until cancelled - exercises the per-call timeout backstop.</summary>
    private sealed class HangingTool : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "hangs";

        public string ParametersSchema => """{"type":"object"}""";

        public async Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ToolResult { Success = true, ResultJson = "{}" };
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
            IToolHost host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult { Success = true, ResultJson = "{}", Artifacts = [artifact] });
    }
}
