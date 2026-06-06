using System.Text.Json;
using FluentAssertions;
using Gert.Console.Tools;
using Gert.Service.Tools;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// The gated local tools (U16): write_file / edit_file / shell route through
/// <see cref="IToolApprover"/> before touching anything. Approve applies and
/// notifies the workspace observer; Deny returns a failure carrying the diff
/// (the model can adapt); the request the approver sees carries the diff the
/// dialog renders.
/// </summary>
public sealed class GatedToolTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceRoot _workspace;
    private readonly RecordingApprover _approver = new();
    private readonly RecordingObserver _observer = new();

    public GatedToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gert-gated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.cs"), "var x = 1;\nvar y = 2;\n");
        _workspace = new WorkspaceRoot(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ToolInvocation Invoke(object args) => new()
    {
        Pid = "default",
        ArgumentsJson = JsonSerializer.Serialize(args),
    };

    /// <summary>Scripted approver that records the requests it was shown.</summary>
    private sealed class RecordingApprover : IToolApprover
    {
        public List<ApprovalRequest> Requests { get; } = [];

        public ApprovalDecision Next { get; set; } = ApprovalDecision.Approve;

        public bool AutoApprove { get; set; }

        public Task<ApprovalDecision> RequestAsync(
            ApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Next);
        }
    }

    private sealed class RecordingObserver : IWorkspaceObserver
    {
        public List<ApprovalRequest> Applied { get; } = [];

        public void OnEditApplied(ApprovalRequest request) => Applied.Add(request);
    }

    [Fact]
    public async Task Approved_write_creates_the_file_and_notifies_the_observer()
    {
        var tool = new WriteFileTool(_workspace, _approver, _observer);

        var result = await tool.ExecuteAsync(Invoke(new { path = "sub/new.txt", content = "hello\n" }));

        result.Success.Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "sub", "new.txt")).Should().Be("hello\n");
        _observer.Applied.Should().ContainSingle().Which.Path.Should().Be(Path.Combine("sub", "new.txt"));
        _approver.Requests.Should().ContainSingle().Which.UnifiedDiff.Should().Contain("+hello");
    }

    [Fact]
    public async Task Denied_write_leaves_the_disk_alone_and_returns_the_diff()
    {
        _approver.Next = ApprovalDecision.Deny;
        var tool = new WriteFileTool(_workspace, _approver, _observer);

        var result = await tool.ExecuteAsync(Invoke(new { path = "app.cs", content = "TAKEOVER" }));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("denied");
        result.ResultJson.Should().Contain("diff");
        File.ReadAllText(Path.Combine(_root, "app.cs")).Should().StartWith("var x = 1;");
        _observer.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task Auto_approve_skips_the_dialog_entirely()
    {
        _approver.AutoApprove = true;
        _approver.Next = ApprovalDecision.Deny; // must never be consulted
        var tool = new WriteFileTool(_workspace, _approver, _observer);

        var result = await tool.ExecuteAsync(Invoke(new { path = "auto.txt", content = "x" }));

        result.Success.Should().BeTrue();
        _approver.Requests.Should().BeEmpty();
        File.Exists(Path.Combine(_root, "auto.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Unchanged_write_is_a_noop_without_approval()
    {
        var tool = new WriteFileTool(_workspace, _approver, _observer);

        var result = await tool.ExecuteAsync(Invoke(new { path = "app.cs", content = "var x = 1;\nvar y = 2;\n" }));

        result.Success.Should().BeTrue();
        result.Stdout.Should().Contain("no write needed");
        _approver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_outside_the_workspace_is_rejected_before_approval()
    {
        var tool = new WriteFileTool(_workspace, _approver, _observer);

        var result = await tool.ExecuteAsync(Invoke(new { path = "../evil.txt", content = "x" }));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("escapes the workspace");
        _approver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Approved_edit_replaces_the_unique_match()
    {
        var tool = new EditFileTool(_workspace, _approver, _observer);

        var result = await tool.ExecuteAsync(Invoke(new
        {
            path = "app.cs",
            old_string = "var x = 1;",
            new_string = "var x = 42;",
        }));

        result.Success.Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "app.cs")).Should().Contain("var x = 42;");
        _observer.Applied.Should().ContainSingle();
        _approver.Requests.Should().ContainSingle().Which.UnifiedDiff
            .Should().Contain("-var x = 1;").And.Contain("+var x = 42;");
    }

    [Fact]
    public async Task Ambiguous_edit_demands_a_unique_anchor()
    {
        var tool = new EditFileTool(_workspace, _approver, _observer);

        var result = await tool.ExecuteAsync(Invoke(new
        {
            path = "app.cs",
            old_string = "var ",
            new_string = "let ",
        }));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("2 times");
        _approver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Replace_all_handles_multiple_matches()
    {
        var tool = new EditFileTool(_workspace, _approver, _observer);

        var result = await tool.ExecuteAsync(Invoke(new
        {
            path = "app.cs",
            old_string = "var ",
            new_string = "let ",
            replace_all = true,
        }));

        result.Success.Should().BeTrue();
        result.Stdout.Should().Contain("2 replacements");
        File.ReadAllText(Path.Combine(_root, "app.cs")).Should().NotContain("var ");
    }

    [Fact]
    public async Task Edit_with_missing_old_string_is_a_tool_error()
    {
        var tool = new EditFileTool(_workspace, _approver, _observer);

        var result = await tool.ExecuteAsync(Invoke(new
        {
            path = "app.cs",
            old_string = "nonexistent",
            new_string = "x",
        }));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Approved_shell_runs_in_the_workspace_and_captures_stdout()
    {
        var tool = new ShellExecTool(_workspace, _approver);

        var result = await tool.ExecuteAsync(Invoke(new { command = "pwd && echo ok" }));

        result.Success.Should().BeTrue();
        result.Stdout.Should().Contain(_workspace.Root).And.Contain("ok");
        _approver.Requests.Should().ContainSingle().Which.Command.Should().Be("pwd && echo ok");
    }

    [Fact]
    public async Task Denied_shell_never_runs()
    {
        _approver.Next = ApprovalDecision.Deny;
        var tool = new ShellExecTool(_workspace, _approver);

        var result = await tool.ExecuteAsync(Invoke(new { command = "touch should-not-exist" }));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("denied");
        File.Exists(Path.Combine(_root, "should-not-exist")).Should().BeFalse();
    }

    [Fact]
    public async Task Failing_shell_command_returns_stderr_as_the_error()
    {
        var tool = new ShellExecTool(_workspace, _approver);

        var result = await tool.ExecuteAsync(Invoke(new { command = "echo broken >&2; exit 3" }));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("broken");
        result.ResultJson.Should().Contain("\"exit_code\":3");
    }

    [Fact]
    public async Task Cancellation_while_awaiting_approval_propagates()
    {
        var blocking = new BlockingApprover();
        var tool = new WriteFileTool(_workspace, blocking, _observer);
        using var cts = new CancellationTokenSource();

        var pending = tool.ExecuteAsync(Invoke(new { path = "x.txt", content = "x" }), cts.Token);
        await blocking.Shown.Task; // the dialog is "open"
        cts.Cancel();

        await pending.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        File.Exists(Path.Combine(_root, "x.txt")).Should().BeFalse();
    }

    /// <summary>An approver that never answers — simulates an open dialog.</summary>
    private sealed class BlockingApprover : IToolApprover
    {
        public TaskCompletionSource Shown { get; } = new();

        public bool AutoApprove { get; set; }

        public async Task<ApprovalDecision> RequestAsync(
            ApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Shown.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return ApprovalDecision.Deny; // unreachable
        }
    }
}
