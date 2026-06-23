using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Gert.Agent.Loop;
using Gert.Model.Agent;
using Gert.Model.Chat;
using Gert.Service.Chat;
using Gert.Testing.Fakes;
using Gert.Tools;
using Gert.Tools.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gert.Agent.Tests;

/// <summary>
/// The reusable tool loop driven DIRECTLY (no chat shell, no repo, no bus): a FakeToolHost + a
/// capturing <see cref="IAgentEventSink"/> stand in for the driver, and Microsoft.Extensions.AI
/// <see cref="IChatClient"/> fakes script the model. These are the loop's own invariants - tool
/// rounds and the M.E.AI working-message shape, the round + per-tool budgets, the per-call entitlement
/// re-check and unentitled-call invisibility, the Modal-timeout exemption + generic backstop, the
/// artifact rideback, the deadline surfacing, and MaxTokensPerRound. The AgentEvent -> ChatEvent
/// mapping, coalescing, citation renumber/persist, and the terminal finalize are the driver's job
/// (the event-log tee) and stay in TurnRunnerTests.
///
/// <para>
/// Convention mirroring the real OpenAIProviderChatClient: a streamed <see cref="FunctionCallContent"/>
/// with null <see cref="FunctionCallContent.Arguments"/> is a live name-first intent (the running
/// card); a non-null arguments dictionary is a completed call.
/// </para>
/// </summary>
public sealed class AgentLoopTests
{
    private const string Pid = "default";
    private const string Conv = "11111111-1111-1111-1111-111111111111";
    private const string AssistantId = "assistant-msg-1";

    private readonly List<AgentEvent> _events = [];

    private IReadOnlyList<AgentEvent> Events => _events;

    /// <summary>The executed tool calls the loop reported (the consumer's persist/render input).</summary>
    private IReadOnlyList<ExecutedToolCall> Executed =>
        _events.OfType<ToolCompleted>().Select(e => e.Call).ToList();

    private CapturingSink Sink() => new(_events);

    private static AgentLoop NewLoop(TimeProvider? clock = null) =>
        new(clock ?? TimeProvider.System, NullLogger<AgentLoop>.Instance);

    private static AgentLoopRequest Request(
        IChatClient model,
        IReadOnlyList<ITool>? tools = null,
        IReadOnlySet<string>? allowed = null,
        IToolHost? host = null,
        TurnOptions? options = null,
        ToolsOptions? toolsOptions = null,
        string userContent = "hello")
    {
        options ??= new TurnOptions();
        tools ??= [];
        var offeredIds = tools.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var allowedIds = allowed ?? tools.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return new AgentLoopRequest
        {
            Messages = [new ChatMessage(ChatRole.User, userContent)],
            Tools = new Toolset(tools, offeredIds, allowedIds, toolsOptions?.PerTool),
            ModelId = "default",
            Model = model,
            Host = host ?? new FakeToolHost(),
            Pid = Pid,
            ConversationId = Conv,
            MessageId = AssistantId,
            MaxRounds = options.MaxToolRounds,
            MaxTokensPerRound = options.MaxTokensPerRound,
        };
    }

    // ---- update builders (mirror OpenAIProviderChatClient's output convention) ----
    private static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);

    private static ChatResponseUpdate Reasoning(string text) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [new TextReasoningContent(text)],
    };

    private static ChatResponseUpdate Intent(string id, string name) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [new FunctionCallContent(id, name)],
    };

    private static ChatResponseUpdate Call(string id, string name, string argumentsJson) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [new FunctionCallContent(id, name, JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)!)],
    };

    private static ChatResponseUpdate Finish(int? completionTokens = null, int? promptTokens = null) => new()
    {
        Role = ChatRole.Assistant,
        FinishReason = ChatFinishReason.Stop,
        Contents = completionTokens is null && promptTokens is null
            ? []
            : [new UsageContent(new UsageDetails { OutputTokenCount = completionTokens, InputTokenCount = promptTokens })],
    };

    private static bool HasToolResult(IEnumerable<ChatMessage> messages) =>
        messages.Any(m => m.Role == ChatRole.Tool);

    private static IReadOnlyList<FunctionCallContent> ToolCalls(ChatMessage message) =>
        message.Contents.OfType<FunctionCallContent>().ToList();

    private static string? ToolResultText(ChatMessage message) =>
        message.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.Result?.ToString();

    /// <summary>Captures every <see cref="AgentEvent"/> the loop emits (the driver's seam, in-memory).</summary>
    private sealed class CapturingSink(List<AgentEvent> events) : IAgentEventSink
    {
        public ValueTask EmitAsync(AgentEvent ev, CancellationToken cancellationToken)
        {
            events.Add(ev);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Tool_round_history_carries_one_assistant_message_with_all_tool_calls()
    {
        var stub = new StubTool();
        var model = new TwoCallToolModel();

        await NewLoop().RunAsync(Request(model, [stub]), Sink());

        // Round 2's upstream history (M.E.AI working shape): ONE assistant message carrying BOTH tool
        // calls of the round (no narration text), then one tool result per call, in call order.
        model.Requests.Should().HaveCount(2);
        var second = model.Requests[1];

        var assistant = second.Single(m => m.Role == ChatRole.Assistant && ToolCalls(m).Count > 0);
        assistant.Text.Should().BeEmpty();
        var calls = ToolCalls(assistant);
        calls.Should().HaveCount(2);
        calls[0].CallId.Should().Be("call_a");
        calls[1].CallId.Should().Be("call_b");

        var results = second.Where(m => m.Role == ChatRole.Tool).ToList();
        results.Should().HaveCount(2);
        results[0].Contents.OfType<FunctionResultContent>().Single().CallId.Should().Be("call_a");
        results[1].Contents.OfType<FunctionResultContent>().Single().CallId.Should().Be("call_b");

        // The assistant tool-call turn precedes its results.
        second.IndexOf(assistant).Should().BeLessThan(second.IndexOf(results[0]));
    }

    [Fact]
    public async Task Round_narration_rides_back_in_the_assistant_tool_call_message()
    {
        var stub = new StubTool();
        var model = new NarratingToolModel();

        await NewLoop().RunAsync(Request(model, [stub]), Sink());

        // The text streamed alongside a round's tool calls is part of the assistant turn (qwen
        // narrates while it calls set_todos). Dropping it makes the model believe its own work never
        // happened and restart the answer next round ("oops, I jumped the gun").
        model.Requests.Should().HaveCount(2);
        var assistant = model.Requests[1].Single(m => m.Role == ChatRole.Assistant && ToolCalls(m).Count > 0);
        assistant.Text.Should().Be("Here is file one.");
        ToolCalls(assistant).Should().ContainSingle();
    }

    [Fact]
    public async Task A_streamed_tool_name_announces_a_running_card_before_the_call_completes()
    {
        var stub = new StubTool();
        await NewLoop().RunAsync(Request(new AnnouncingToolModel(), [stub], userContent: "go"), Sink());

        // The live-intent announce (name only, mid-stream) and the end-of-round started event (now
        // with args) share the call id - two ToolStarted events for one call; the consumer dedupes by
        // id into a single card.
        var started = Events.OfType<ToolStarted>().Where(e => e.CallId == "call_x").ToList();
        started.Should().HaveCount(2);
        started[0].Request.Should().BeNull("the mid-stream announce has no parsed args yet");
        started[1].Request.Should().NotBeNull("the end-of-round event carries the parsed args");

        // The announce precedes the tool's completion (it was emitted while streaming).
        var types = Events.Select(e => e.GetType().Name).ToList();
        types.IndexOf(nameof(ToolStarted))
            .Should().BeLessThan(types.IndexOf(nameof(ToolCompleted)));
    }

    [Fact]
    public async Task Entitlement_snapshot_blocks_execution_silently_even_when_the_spec_reached_the_model()
    {
        // The stub spec is (improperly) offered, but the entitlement snapshot does not include it -
        // the off-thread second-line defence must refuse. The refusal is invisible: NO started/completed
        // event; only a synthetic refusal is fed upstream, which the model reads and answers around.
        var stub = new StubTool();

        await NewLoop().RunAsync(
            Request(
                new AnnouncingToolModel(),
                [stub],
                allowed: new HashSet<string>(["rag"], StringComparer.Ordinal),
                userContent: "go"),
            Sink());

        // Not a flicker of activity for the refused call - no running announce, no completed event.
        Events.OfType<ToolStarted>().Should().BeEmpty();
        Events.OfType<ToolCompleted>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_tool_that_returns_an_artifact_carries_it_on_the_completed_call()
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

        await NewLoop().RunAsync(Request(new TextThenToolModel(), [tool]), Sink());

        // The artifact rides the ToolCompleted event (the consumer emits the ArtifactEvent after the
        // tool_result card and persists the row).
        var completed = Events.OfType<ToolCompleted>().Single();
        var ridden = completed.Call.Artifacts.Should().ContainSingle().Which;
        ridden.Id.Should().Be("art-9");
        ridden.Kind.Should().Be(ArtifactKind.Html);
        ridden.Name.Should().Be("demo.html");
        ridden.Content.Should().Be("<h1>Demo</h1>");
    }

    [Fact]
    public async Task Runaway_tool_loop_is_bounded_and_returns_with_the_streamed_content()
    {
        var stub = new StubTool();
        var model = new AlwaysToolCallingModel();

        // A model that requests a tool on EVERY round. Past the cap the loop refuses the calls, winds
        // down once with no tools advertised, and when even that round emits tool calls it stops.
        var result = await NewLoop().RunAsync(
            Request(model, [stub], options: new TurnOptions { MaxToolRounds = 5 }), Sink());

        // 5 executed rounds + 1 refused round + 1 wind-down round = 7 upstream calls, hard stop.
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

        await NewLoop().RunAsync(Request(model, [stub], options: new TurnOptions { MaxToolRounds = 5 }), Sink());

        // The refused round still answers each call in the upstream history - ONE assistant message
        // carrying the round's narration + tool calls, then a budget-exhausted tool result per call.
        var windDown = model.Requests[6];
        var assistant = windDown.Last(m => m.Role == ChatRole.Assistant && ToolCalls(m).Count > 0);
        ToolCalls(assistant).Should().ContainSingle();
        assistant.Text.Should().Contain("round 6");

        var toolMsg = windDown.Last(m => m.Role == ChatRole.Tool);
        toolMsg.Contents.OfType<FunctionResultContent>().Single().CallId
            .Should().Be(ToolCalls(assistant)[0].CallId);
        ToolResultText(toolMsg).Should().Contain("tool budget exhausted");

        // Five executed-done calls; the refused round's call is an error.
        Executed.Count(r => r.Status == ToolCallStatus.Done).Should().Be(5);
        Executed.Count(r => r.Status == ToolCallStatus.Error).Should().Be(1);

        // The cap-trip is VISIBLE: the refused call carries the budget message for the card's error line.
        var refused = Executed.Single(r => r.Status == ToolCallStatus.Error);
        refused.Error.Should().Contain("tool budget exhausted");
    }

    [Fact]
    public async Task A_tools_intrinsic_call_cap_refuses_calls_past_the_cap()
    {
        // The tool ships its own MaxCallsPerTurn=2 (web_search's intrinsic ceiling pattern); no
        // operator config involved. Two calls execute, the rest refuse with a synthetic result.
        var search = new CountingSearchTool(ToolBounds.Default with { MaxCallsPerTurn = 2 });
        var model = new SearchingModel(rounds: 4);

        await NewLoop().RunAsync(Request(model, [search]), Sink());

        search.Executions.Should().Be(2);
        Executed.Count(r => r.Status == ToolCallStatus.Done).Should().Be(2);
        Executed.Count(r => r.Status == ToolCallStatus.Error).Should().Be(2);

        var refused = Executed.Where(r => r.Status == ToolCallStatus.Error).ToList();
        refused.Should().HaveCount(2);
        refused.Should().OnlyContain(e => e.Error!.Contains("call budget exhausted"));
    }

    [Fact]
    public async Task A_per_tool_config_override_sets_the_call_cap()
    {
        // The tool keeps its generous default; an operator Gert:Tools:<id>:Limits override tightens
        // MaxCallsPerTurn to 2. Same refusal behaviour, sourced from config not the tool.
        var search = new CountingSearchTool();
        var toolsOptions = new ToolsOptions();
        toolsOptions.PerTool["search"] = new ToolBoundsOverride { MaxCallsPerTurn = 2 };
        var model = new SearchingModel(rounds: 4);

        await NewLoop().RunAsync(Request(model, [search], toolsOptions: toolsOptions), Sink());

        search.Executions.Should().Be(2);
        Executed.Count(r => r.Status == ToolCallStatus.Error).Should().Be(2);
        Executed.Where(r => r.Status == ToolCallStatus.Error)
            .Should().OnlyContain(e => e.Error!.Contains("call budget exhausted"));
    }

    [Fact]
    public async Task A_disabled_call_cap_lets_every_call_run()
    {
        // MaxCallsPerTurn <= 0 is unlimited (the disable sentinel) - every call runs.
        var search = new CountingSearchTool();
        var toolsOptions = new ToolsOptions();
        toolsOptions.PerTool["search"] = new ToolBoundsOverride { MaxCallsPerTurn = 0 };
        var model = new SearchingModel(rounds: 4);

        await NewLoop().RunAsync(Request(model, [search], toolsOptions: toolsOptions), Sink());

        search.Executions.Should().Be(4);
        Executed.Should().OnlyContain(r => r.Status == ToolCallStatus.Done);
    }

    [Fact]
    public async Task Per_round_max_tokens_is_the_configured_bound_else_unset()
    {
        // Every round carries the configured MaxTokensPerRound, or nothing when it is disabled (0),
        // leaving the provider's own default to apply.
        var model = new RequestCapturingModel();

        await NewLoop().RunAsync(Request(model, options: new TurnOptions { MaxTokensPerRound = 100 }), Sink());
        await NewLoop().RunAsync(Request(model, options: new TurnOptions { MaxTokensPerRound = 0 }), Sink());

        model.MaxTokens.Should().Equal(new int?[] { 100, null });
    }

    [Fact]
    public async Task Hung_tool_call_times_out_on_its_intrinsic_bounds_and_the_loop_completes()
    {
        // The tool's own Bounds.CallTimeout (50 ms) is the backstop - no operator config.
        var hung = new HangingTool(ToolBounds.Default with { CallTimeout = TimeSpan.FromMilliseconds(50) });

        await NewLoop().RunAsync(Request(new TextThenToolModel(), [hung]), Sink());

        // The call failed visibly - card error text names the timeout - and the loop went on to a
        // final answer instead of dying with it.
        var completed = Executed.Single();
        completed.Status.Should().Be(ToolCallStatus.Error);
        completed.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task A_per_tool_config_override_sets_the_call_timeout()
    {
        // The tool keeps its 60 s default; a Gert:Tools:<id>:Limits override tightens CallTimeout to
        // 50 ms, which trips the hung call.
        var hung = new HangingTool();
        var toolsOptions = new ToolsOptions();
        toolsOptions.PerTool["stub"] = new ToolBoundsOverride { CallTimeout = TimeSpan.FromMilliseconds(50) };

        await NewLoop().RunAsync(Request(new TextThenToolModel(), [hung], toolsOptions: toolsOptions), Sink());

        var completed = Executed.Single();
        completed.Status.Should().Be(ToolCallStatus.Error);
        completed.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task An_interactive_tool_is_exempt_from_the_per_call_timeout()
    {
        // A Modal tool must outlive its CallTimeout (blocking IS its job): the loop skips the per-call
        // backstop for modal tools, even with a tight intrinsic CallTimeout.
        var slow = new SlowInteractiveTool(
            TimeSpan.FromMilliseconds(300), ToolBounds.Default with { CallTimeout = TimeSpan.FromMilliseconds(50) });

        await NewLoop().RunAsync(Request(new TextThenToolModel(), [slow]), Sink());

        Executed.Single().Status.Should().Be(ToolCallStatus.Done);
    }

    [Fact]
    public async Task A_tools_effective_token_budget_reaches_its_host_limits()
    {
        // The per-tool TokenBudget (intrinsic, overridable) is copied onto the per-call host the tool
        // is handed - BudgetedToolHost feeds the existing ToolLimits.TokenBudget seam.
        var probe = new TokenBudgetProbeTool(ToolBounds.Default with { TokenBudget = 4242 });

        await NewLoop().RunAsync(Request(new TextThenToolModel(), [probe]), Sink());

        probe.TokenBudgetSeen.Should().Be(4242);
    }

    [Fact]
    public async Task A_per_tool_config_override_sets_the_token_budget()
    {
        var probe = new TokenBudgetProbeTool();
        var toolsOptions = new ToolsOptions();
        toolsOptions.PerTool["stub"] = new ToolBoundsOverride { TokenBudget = 99 };

        await NewLoop().RunAsync(Request(new TextThenToolModel(), [probe], toolsOptions: toolsOptions), Sink());

        probe.TokenBudgetSeen.Should().Be(99);
    }

    [Fact]
    public async Task The_invocation_surfaces_the_host_deadline_for_a_tools_budget_math()
    {
        // The loop stamps each invocation with the host's deadline so an interactive tool can budget
        // its wait against the turn (null here; the chat shell wires the real deadline - TurnRunnerTests).
        var probe = new DeadlineProbeTool();

        await NewLoop().RunAsync(Request(new TextThenToolModel(), [probe]), Sink());

        probe.DeadlineSeen.Should().BeTrue();
    }

    [Fact]
    public async Task An_autonomous_run_with_the_discard_sink_still_returns_the_final_answer()
    {
        // An autonomous driver (sub-agent / headless) runs the loop with the discard sink: the events
        // are dropped, nothing persists, and the loop still returns the final answer.
        var stub = new StubTool();

        var result = await NewLoop().RunAsync(Request(new TextThenToolModel(), [stub]), NullAgentEventSink.Instance);

        result.Content.Should().Be("checking done");
    }

    // model fakes (loop-local IChatClient; the chat-shell fakes live in TurnRunnerTests)

    /// <summary>Base IChatClient: streaming only (the loop never calls the buffered path).</summary>
    private abstract class ScriptedClient : IChatClient
    {
        public abstract IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("scripted fake streams only");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Round 1: text then a tool call. Round 2: a final answer.</summary>
    private sealed class TextThenToolModel : ScriptedClient
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!HasToolResult(messages))
            {
                yield return Text("checking ");
                yield return Call("call_1", "stub_tool", "{}");
            }
            else
            {
                yield return Text("done");
            }

            yield return Finish();
            await Task.Yield();
        }
    }

    /// <summary>Round 1: two tool calls. Round 2: a final answer. Captures each request's messages.</summary>
    private sealed class TwoCallToolModel : ScriptedClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());

            if (!HasToolResult(messages))
            {
                yield return Call("call_a", "stub_tool", "{}");
                yield return Call("call_b", "stub_tool", """{"x":1}""");
            }
            else
            {
                yield return Text("done");
            }

            yield return Finish();
            await Task.Yield();
        }
    }

    /// <summary>Round 1 streams narration text AND a tool call; round 2 finishes.</summary>
    private sealed class NarratingToolModel : ScriptedClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());

            if (!HasToolResult(messages))
            {
                yield return Text("Here is file one.");
                yield return Call("call_a", "stub_tool", "{}");
            }
            else
            {
                yield return Text(" And file two.");
            }

            yield return Finish();
            await Task.Yield();
        }
    }

    /// <summary>
    /// Requests a tool on EVERY round - the runaway tool loop. Captures each request's messages and
    /// advertised tools.
    /// </summary>
    private sealed class AlwaysToolCallingModel : ScriptedClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public List<IReadOnlyList<AITool>> Tools { get; } = [];

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            Tools.Add(options?.Tools?.ToList() ?? []);

            yield return Text($"round {Requests.Count} ");
            yield return Call($"call_{Requests.Count}", "stub_tool", "{}");
            yield return Finish();
            await Task.Yield();
        }
    }

    /// <summary>Calls <c>web_search</c> for the first N rounds, then answers plain text.</summary>
    private sealed class SearchingModel(int rounds) : ScriptedClient
    {
        private int _round;

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _round++;
            if (_round <= rounds)
            {
                yield return Call($"call_{_round}", "web_search", """{"query":"x"}""");
            }
            else
            {
                yield return Text("done");
            }

            yield return Finish();
            await Task.Yield();
        }
    }

    /// <summary>Plain text answer, capturing each request's <c>MaxOutputTokens</c>.</summary>
    private sealed class RequestCapturingModel : ScriptedClient
    {
        public List<int?> MaxTokens { get; } = [];

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            MaxTokens.Add(options?.MaxOutputTokens);
            yield return Text("ok");
            yield return Finish();
            await Task.Yield();
        }
    }

    /// <summary>
    /// Round 1: a text delta, a tool-call NAME intent (null args), then the completed call -
    /// exercising the early live-intent path. Round 2: answers.
    /// </summary>
    private sealed class AnnouncingToolModel : ScriptedClient
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!HasToolResult(messages))
            {
                yield return Text("working ");
                yield return Intent("call_x", "stub_tool");
                yield return Call("call_x", "stub_tool", "{}");
            }
            else
            {
                yield return Text("done");
            }

            yield return Finish();
            await Task.Yield();
        }
    }

    // ----- tool fakes (ITool; unchanged by the M.E.AI migration) -----

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

    /// <summary>A "search"-id tool that counts how often it executes; carries a settable call cap.</summary>
    private sealed class CountingSearchTool(ToolBounds? bounds = null) : ITool
    {
        private int _executions;

        public int Executions => _executions;

        public string Id => "search";

        public string Name => "web_search";

        public string Description => "a counting search";

        public string ParametersSchema => """{"type":"object"}""";

        public ToolBounds Bounds { get; } = bounds ?? ToolBounds.Default;

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executions);
            return Task.FromResult(new ToolResult { Success = true, ResultJson = "{}" });
        }
    }

    /// <summary>A Modal tool that takes longer than its own CallTimeout (which it is exempt from).</summary>
    private sealed class SlowInteractiveTool(TimeSpan wait, ToolBounds? bounds = null) : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "waits on the user";

        public string ParametersSchema => """{"type":"object"}""";

        public ToolType Type => ToolType.Modal;

        public ToolBounds Bounds { get; } = bounds ?? ToolBounds.Default;

        public async Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(wait, cancellationToken);
            return new ToolResult { Success = true, ResultJson = "{}" };
        }
    }

    /// <summary>Records whether the loop stamped the invocation with the host's deadline.</summary>
    private sealed class DeadlineProbeTool : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "probes the deadline";

        public string ParametersSchema => """{"type":"object"}""";

        public bool DeadlineSeen { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default)
        {
            DeadlineSeen = invocation.Deadline == host.Limits.Deadline;
            return Task.FromResult(new ToolResult { Success = true, ResultJson = "{}" });
        }
    }

    /// <summary>Hangs until cancelled - exercises the per-call timeout backstop; carries a settable CallTimeout.</summary>
    private sealed class HangingTool(ToolBounds? bounds = null) : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "hangs";

        public string ParametersSchema => """{"type":"object"}""";

        public ToolBounds Bounds { get; } = bounds ?? ToolBounds.Default;

        public async Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ToolResult { Success = true, ResultJson = "{}" };
        }
    }

    /// <summary>Records the per-call <see cref="ToolLimits.TokenBudget"/> the host handed it (a probe).</summary>
    private sealed class TokenBudgetProbeTool(ToolBounds? bounds = null) : ITool
    {
        public string Id => "stub";

        public string Name => "stub_tool";

        public string Description => "probes the token budget";

        public string ParametersSchema => """{"type":"object"}""";

        public ToolBounds Bounds { get; } = bounds ?? ToolBounds.Default;

        public int? TokenBudgetSeen { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            IToolHost host,
            CancellationToken cancellationToken = default)
        {
            TokenBudgetSeen = host.Limits.TokenBudget;
            return Task.FromResult(new ToolResult { Success = true, ResultJson = "{}" });
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
            CancellationToken cancellationToken = default)
        {
            host.Card.ReportArtifact(artifact);
            return Task.FromResult(new ToolResult { Success = true, ResultJson = "{}" });
        }
    }
}
