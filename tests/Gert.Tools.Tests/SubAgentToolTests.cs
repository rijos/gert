using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Gert.Chat;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Service.Chat;
using Gert.Service.External;
using Gert.Service.Tools;
using Gert.Testing.Fakes;
using Gert.Tools.Builtin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Tools.Tests;

/// <summary>
/// The sub-agent tool (chat-and-tools.md section sub-agent): the nested loop
/// returns only the final text, nested tools are delegable AND entitled (the
/// claim stays the ceiling), recursion is structurally impossible, and the
/// failure paths (no provider, bad args, round cap) degrade to model-readable
/// errors instead of throwing.
/// </summary>
public sealed class SubAgentToolTests
{
    private const string Pid = "default";

    private static readonly IReadOnlySet<string> AllTools =
        new HashSet<string>(["rag", "search", "fetch", "clock", "sub_agent"], StringComparer.Ordinal);

    /// <summary>Scripted client: replays the given rounds; captures every request.</summary>
    private sealed class ScriptedModel : IChatModelClient
    {
        private readonly Queue<ChatModelChunk[]> _rounds;

        public List<ChatCompletionRequest> Requests { get; } = [];

        public ScriptedModel(params ChatModelChunk[][] rounds)
        {
            _rounds = new Queue<ChatModelChunk[]>(rounds);
        }

        public async IAsyncEnumerable<ChatModelChunk> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var round = _rounds.Count > 0
                ? _rounds.Dequeue()
                : [new ChatModelChunk { TextDelta = "default answer", FinishReason = "stop" }];
            foreach (var chunk in round)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunk;
            }
        }
    }

    private static SubAgentTool NewTool(IChatModelClient model)
    {
        // A real scope with the one delegable tool the tests exercise: the
        // clock (deterministic via the pinned TimeProvider).
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ITool, ClockTool>();
        var provider = services.BuildServiceProvider();
        return new SubAgentTool(
            provider,
            new FixedChatClientFactory(model),
            Options.Create(new TurnOptions()),
            TimeProvider.System,
            NullLogger<SubAgentTool>.Instance);
    }

    private static ToolInvocation Invocation(
        string args,
        string? modelId = "test-provider",
        IReadOnlySet<string>? allowed = null) => new()
    {
        Pid = Pid,
        ArgumentsJson = args,
        ConversationId = "conv-1",
        ModelId = modelId,
        AllowedToolIds = allowed ?? AllTools,
    };

    private static ChatModelChunk[] FinalText(string text) =>
        [new ChatModelChunk { TextDelta = text, FinishReason = "stop" }];

    private static ChatModelChunk[] CallClock(string callId) =>
    [
        new ChatModelChunk
        {
            ToolCall = new ChatModelToolCall
            {
                Id = callId,
                Name = "get_datetime",
                ArgumentsJson = "{}",
            },
            FinishReason = "tool_calls",
        },
    ];

    [Fact]
    public async Task Returns_the_final_text_only()
    {
        var model = new ScriptedModel(FinalText("the digested result"));
        var result = await NewTool(model).ExecuteAsync(Invocation("""{"task":"digest this"}"""));

        result.Success.Should().BeTrue();
        result.Stdout.Should().Be("the digested result");
        using var doc = JsonDocument.Parse(result.ResultJson!);
        doc.RootElement.GetProperty("result").GetString().Should().Be("the digested result");
        doc.RootElement.GetProperty("rounds").GetInt32().Should().Be(1);

        // The nested conversation is fresh: a system prompt + the task, nothing
        // of the parent's history.
        var first = model.Requests.Single();
        first.Messages.Should().HaveCount(2);
        first.Messages[0].Role.Should().Be("system");
        first.Messages[1].Content.Should().Be("digest this");
    }

    [Fact]
    public async Task Context_rides_the_task_message()
    {
        var model = new ScriptedModel(FinalText("ok"));
        await NewTool(model).ExecuteAsync(
            Invocation("""{"task":"summarize","context":"raw material"}"""));

        model.Requests.Single().Messages[1].Content
            .Should().Contain("summarize").And.Contain("raw material");
    }

    [Fact]
    public async Task Nested_tool_round_trip_feeds_the_result_back()
    {
        var model = new ScriptedModel(CallClock("c1"), FinalText("it is late"));
        var result = await NewTool(model).ExecuteAsync(Invocation("""{"task":"what time"}"""));

        result.Success.Should().BeTrue();
        result.Stdout.Should().Be("it is late");

        // Round 2 carries the assistant tool-calls message + the clock's result.
        var second = model.Requests[1];
        second.Messages.Should().HaveCount(4);
        second.Messages[2].Role.Should().Be("assistant");
        second.Messages[2].ToolCalls.Should().ContainSingle(c => c.Id == "c1");
        second.Messages[3].Role.Should().Be("tool");
        second.Messages[3].ToolCallId.Should().Be("c1");
        second.Messages[3].Content.Should().Contain("utc");
    }

    [Fact]
    public async Task Advertised_tools_are_delegable_AND_entitled()
    {
        // Entitled to everything -> the scope's one delegable tool (clock) is offered.
        var model = new ScriptedModel(FinalText("ok"));
        await NewTool(model).ExecuteAsync(Invocation("""{"task":"t"}"""));
        model.Requests.Single().Tools.Should().ContainSingle(t => t.Name == "get_datetime");

        // Clock outside the parent's entitlement snapshot -> nothing is offered.
        var bare = new ScriptedModel(FinalText("ok"));
        await NewTool(bare).ExecuteAsync(Invocation(
            """{"task":"t"}""",
            allowed: new HashSet<string>(["sub_agent"], StringComparer.Ordinal)));
        bare.Requests.Single().Tools.Should().BeEmpty();
    }

    [Fact]
    public async Task Sub_agent_is_never_offered_to_itself()
    {
        // Even fully entitled, run_sub_agent is not in the delegable set: the
        // nested loop cannot recurse.
        var model = new ScriptedModel(FinalText("ok"));
        await NewTool(model).ExecuteAsync(Invocation("""{"task":"t"}"""));
        model.Requests.Single().Tools.Should().NotContain(t => t.Name == "run_sub_agent");
    }

    [Fact]
    public async Task Unknown_nested_call_degrades_to_a_readable_error()
    {
        var rogue = new ChatModelChunk
        {
            ToolCall = new ChatModelToolCall { Id = "c9", Name = "run_python", ArgumentsJson = "{}" },
            FinishReason = "tool_calls",
        };
        var model = new ScriptedModel([rogue], FinalText("recovered"));
        var result = await NewTool(model).ExecuteAsync(Invocation("""{"task":"t"}"""));

        result.Success.Should().BeTrue("the sub-agent reads the error and answers anyway");
        // The default JSON encoder escapes quotes, so assert the parts.
        model.Requests[1].Messages[3].Content.Should().Contain("no tool named").And.Contain("run_python");
    }

    [Fact]
    public async Task No_provider_snapshot_fails_closed()
    {
        var model = new ScriptedModel();
        var result = await NewTool(model).ExecuteAsync(
            Invocation("""{"task":"t"}""", modelId: null));

        result.Success.Should().BeFalse();
        model.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"task":"  "}""")]
    public async Task Bad_arguments_are_model_correctable_errors(string args)
    {
        var result = await NewTool(new ScriptedModel()).ExecuteAsync(Invocation(args));
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Round_cap_fails_instead_of_spinning_forever()
    {
        // A model that asks for the clock every round never converges; the cap
        // turns that into a visible error, not an infinite loop.
        var rounds = Enumerable.Range(0, 32).Select(i => CallClock($"c{i}")).ToArray();
        var model = new ScriptedModel(rounds);
        var result = await NewTool(model).ExecuteAsync(Invocation("""{"task":"t"}"""));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("rounds");
        model.Requests.Count.Should().Be(16);
    }
}
