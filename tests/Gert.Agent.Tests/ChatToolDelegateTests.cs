using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Gert.Agent.Hosting;
using Gert.Agent.Loop;
using Gert.Service.Chat;
using Gert.Testing.Fakes;
using Gert.Tools;
using Gert.Tools.Builtin;
using Gert.Tools.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gert.Agent.Tests;

/// <summary>
/// <see cref="ChatToolDelegate"/> (chat-and-tools.md section sub-agent): the nested loop driver the
/// <c>run_sub_agent</c> tool talks to through <see cref="IToolDelegate"/>. It builds a FRESH nested
/// conversation (system prompt + the task, none of the parent history), runs the SAME
/// <see cref="IAgentLoop"/> against the autonomous nested host, and returns only the final text.
/// These pin the moved-out behaviour: caps, the fresh conversation shape, a nested tool round-trip,
/// and the round count - the delegable-set intersection itself is the driver's (TurnRunner's) job
/// and is asserted there + in the SubAgentTool tests' fake.
/// </summary>
public sealed class ChatToolDelegateTests
{
    private const string ModelId = "test-provider";

    private static AgentLoop NewLoop() => new(TimeProvider.System, NullLogger<AgentLoop>.Instance);

    private static IToolHost AutonomousHost() => new FakeToolHost { Ui = null };

    private static ChatToolDelegate NewDelegate(
        IChatClient model,
        IReadOnlyList<ITool>? delegable = null,
        IToolHost? nestedHost = null)
    {
        delegable ??= [];
        return new ChatToolDelegate(
            NewLoop(),
            model,
            ModelId,
            delegable,
            delegable.Select(t => t.Id).ToHashSet(StringComparer.Ordinal),
            nestedHost ?? AutonomousHost(),
            maxTokensPerRound: null,
            perTool: new Dictionary<string, ToolBoundsOverride>());
    }

    private static ChatResponseUpdate[] FinalText(string text) => [Text(text)];

    private static ChatResponseUpdate[] CallClock(string callId) => [Call(callId, "get_datetime", "{}")];

    private static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);

    private static ChatResponseUpdate Call(string id, string name, string argumentsJson) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [new FunctionCallContent(id, name, JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)!)],
    };

    private static ClockTool NewClock() => new(Gert.Testing.Proof.Validation, TimeProvider.System);

    [Fact]
    public async Task Runs_the_loop_and_returns_the_final_text_only()
    {
        var model = new ScriptedModel(FinalText("the digested result"));
        var result = await NewDelegate(model).RunAsync(new DelegateRequest("digest this", null));

        result.Success.Should().BeTrue();
        result.Text.Should().Be("the digested result");
        result.Rounds.Should().Be(1);

        // A fresh nested conversation: system prompt + the task, nothing of the parent.
        var first = model.Requests.Single();
        first.Messages.Should().HaveCount(2);
        first.Messages[0].Role.Should().Be(ChatRole.System);
        first.Messages[1].Text.Should().Be("digest this");
    }

    [Fact]
    public async Task Context_rides_the_task_message()
    {
        var model = new ScriptedModel(FinalText("ok"));
        await NewDelegate(model).RunAsync(new DelegateRequest("summarize", "raw material"));

        model.Requests.Single().Messages[1].Text
            .Should().Contain("summarize").And.Contain("raw material");
    }

    [Fact]
    public async Task A_nested_tool_round_trip_feeds_the_result_back_then_answers()
    {
        var model = new ScriptedModel(CallClock("c1"), FinalText("it is late"));
        var result = await NewDelegate(model, [NewClock()]).RunAsync(new DelegateRequest("what time", null));

        result.Success.Should().BeTrue();
        result.Text.Should().Be("it is late");
        result.Rounds.Should().Be(2);

        // Round 2 carries the assistant tool-calls message + the clock's result.
        var second = model.Requests[1];
        second.Messages.Should().Contain(m =>
            m.Role == ChatRole.Tool && m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == "c1"));
    }

    [Fact]
    public async Task The_delegable_tools_are_advertised_to_the_nested_model()
    {
        var model = new ScriptedModel(FinalText("ok"));
        await NewDelegate(model, [NewClock()]).RunAsync(new DelegateRequest("t", null));

        model.Requests.Single().Tools.Should().ContainSingle(t => t.Name == "get_datetime");
    }

    [Fact]
    public async Task An_empty_task_is_a_model_correctable_error()
    {
        var result = await NewDelegate(new ScriptedModel()).RunAsync(new DelegateRequest("   ", null));

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task An_oversized_task_or_context_fails_the_caps()
    {
        var del = NewDelegate(new ScriptedModel());

        (await del.RunAsync(new DelegateRequest(new string('x', 8_001), null)))
            .Success.Should().BeFalse();
        (await del.RunAsync(new DelegateRequest("ok", new string('x', 32_001))))
            .Success.Should().BeFalse();
    }

    [Fact]
    public async Task The_nested_host_is_autonomous_no_ui()
    {
        // The nested host carries Ui=null: a sub-agent can never ask_user. The loop
        // itself ran (a final answer came back), so the autonomous host is wired.
        var host = AutonomousHost();
        host.Ui.Should().BeNull();

        var model = new ScriptedModel(FinalText("done"));
        var result = await NewDelegate(model, nestedHost: host).RunAsync(new DelegateRequest("t", null));
        result.Text.Should().Be("done");
    }

    /// <summary>Scripted client: replays the given rounds; captures every request's messages + tools.</summary>
    private sealed class ScriptedModel : IChatClient
    {
        private readonly Queue<ChatResponseUpdate[]> _rounds;

        public ScriptedModel(params ChatResponseUpdate[][] rounds)
        {
            _rounds = new Queue<ChatResponseUpdate[]>(rounds);
        }

        public List<Captured> Requests { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(new Captured(messages.ToList(), options?.Tools?.ToList() ?? []));
            var round = _rounds.Count > 0 ? _rounds.Dequeue() : [Text("default answer")];
            foreach (var update in round)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return update;
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("scripted fake streams only");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        /// <summary>One captured round: the working messages and the advertised tools.</summary>
        public sealed record Captured(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<AITool> Tools);
    }
}
