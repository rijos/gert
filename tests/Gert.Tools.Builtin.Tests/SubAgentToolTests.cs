using System.Text.Json;
using FluentAssertions;
using Gert.Testing.Fakes;
using Gert.Tools;
using Gert.Tools.Hosting;
using Xunit;

namespace Gert.Tools.Builtin.Tests;

/// <summary>
/// The sub-agent tool (chat-and-tools.md section sub-agent): it parses + bounds the model's
/// {task, context} args (model-correctable errors stay here), drives the host's
/// <see cref="IToolDelegate"/> with a <see cref="DelegateRequest"/>, and shapes the
/// <see cref="DelegateResult"/> back into a <see cref="ToolResult"/>. The nested loop, the
/// delegable-set intersection, and the autonomous host live behind the delegate (ChatToolDelegate)
/// and are tested in ChatToolDelegateTests - here the delegate is a scriptable fake.
/// </summary>
public sealed class SubAgentToolTests
{
    private static FakeToolHost HostWith(FakeToolDelegate del) => new() { Delegate = del };

    private static ToolInvocation Invocation(string args) => new()
    {
        Pid = "default",
        ArgumentsJson = args,
        ConversationId = "conv-1",
    };

    [Fact]
    public async Task Parses_task_and_context_and_calls_the_delegate()
    {
        var del = new FakeToolDelegate();
        await new SubAgentTool().ExecuteAsync(
            Invocation("""{"task":"summarize","context":"raw material"}"""), HostWith(del));

        del.LastRequest.Should().NotBeNull();
        del.LastRequest!.Task.Should().Be("summarize");
        del.LastRequest.Context.Should().Be("raw material");
    }

    [Fact]
    public async Task A_successful_delegate_result_becomes_the_tool_result()
    {
        var del = new FakeToolDelegate
        {
            Result = new DelegateResult { Success = true, Text = "the digested result", Rounds = 3 },
        };

        var result = await new SubAgentTool().ExecuteAsync(
            Invocation("""{"task":"digest this"}"""), HostWith(del));

        result.Success.Should().BeTrue();
        result.Stdout.Should().Be("the digested result");
        using var doc = JsonDocument.Parse(result.ResultJson!);
        doc.RootElement.GetProperty("result").GetString().Should().Be("the digested result");
        doc.RootElement.GetProperty("rounds").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task A_failed_delegate_result_becomes_a_failed_tool_result()
    {
        var del = new FakeToolDelegate
        {
            Result = new DelegateResult { Success = false, Error = "sub-agent ran out of time" },
        };

        var result = await new SubAgentTool().ExecuteAsync(
            Invocation("""{"task":"t"}"""), HostWith(del));

        result.Success.Should().BeFalse();
        result.Error.Should().Be("sub-agent ran out of time");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"task":"  "}""")]
    public async Task Bad_arguments_are_model_correctable_errors_and_never_delegate(string args)
    {
        var del = new FakeToolDelegate();
        var result = await new SubAgentTool().ExecuteAsync(Invocation(args), HostWith(del));

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        del.LastRequest.Should().BeNull("a malformed call must never reach the delegate");
    }

    [Theory]
    [InlineData(8_001, 0)]
    [InlineData(10, 32_001)]
    public async Task Oversized_task_or_context_fails_before_delegating(int taskLen, int contextLen)
    {
        var del = new FakeToolDelegate();
        var task = new string('x', taskLen);
        var context = contextLen > 0 ? new string('x', contextLen) : null;
        var args = JsonSerializer.Serialize(new { task, context });

        var result = await new SubAgentTool().ExecuteAsync(Invocation(args), HostWith(del));

        result.Success.Should().BeFalse();
        del.LastRequest.Should().BeNull();
    }
}
