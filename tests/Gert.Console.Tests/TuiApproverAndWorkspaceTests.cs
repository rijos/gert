using FluentAssertions;
using Gert.Console.Tools;
using Gert.Console.Tui.State;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// The TUI-side approval bridge + workspace pane model (U16): the handler
/// receives the request and its verdict flows back to the blocked tool;
/// auto-approve and the no-handler fail-safe short-circuit; cancellation (a
/// turn stop while the dialog is open) propagates. The workspace presenter
/// keeps the latest diff per touched file.
/// </summary>
public sealed class TuiApproverAndWorkspaceTests
{
    private static ApprovalRequest Request(string path = "a.txt") => new()
    {
        Kind = "write_file",
        Path = path,
        UnifiedDiff = $"--- a/{path}\n+++ b/{path}\n@@ -0,0 +1,1 @@\n+x\n",
    };

    [Fact]
    public async Task Handler_decision_flows_back_to_the_tool()
    {
        var approver = new TuiToolApprover();
        ApprovalRequest? shown = null;
        approver.Handler = (request, _) =>
        {
            shown = request;
            return Task.FromResult(ApprovalDecision.Deny);
        };

        var decision = await approver.RequestAsync(Request());

        decision.Should().Be(ApprovalDecision.Deny);
        shown!.UnifiedDiff.Should().Contain("+x");
    }

    [Fact]
    public async Task Auto_approve_short_circuits_the_handler()
    {
        var approver = new TuiToolApprover { AutoApprove = true };
        var asked = false;
        approver.Handler = (_, _) =>
        {
            asked = true;
            return Task.FromResult(ApprovalDecision.Deny);
        };

        var decision = await approver.RequestAsync(Request());

        decision.Should().Be(ApprovalDecision.Approve);
        asked.Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_handler_gated_actions_are_denied()
    {
        var approver = new TuiToolApprover();

        var decision = await approver.RequestAsync(Request());

        decision.Should().Be(ApprovalDecision.Deny);
    }

    [Fact]
    public async Task A_turn_stop_cancels_the_open_dialog()
    {
        var approver = new TuiToolApprover();
        var dialogOpen = new TaskCompletionSource();
        approver.Handler = async (_, ct) =>
        {
            dialogOpen.SetResult();
            // The dialog idles until the user answers — or the turn dies.
            await Task.Delay(Timeout.Infinite, ct);
            return ApprovalDecision.Approve;
        };

        using var cts = new CancellationTokenSource();
        var pending = approver.RequestAsync(Request(), cts.Token);
        await dialogOpen.Task;
        cts.Cancel();

        await pending.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Workspace_presenter_keeps_the_latest_diff_per_file()
    {
        var presenter = new WorkspacePresenter();
        var changes = 0;
        presenter.Changed += () => changes++;

        presenter.OnEditApplied(Request("one.cs"));
        presenter.OnEditApplied(Request("two.cs"));
        presenter.OnEditApplied(new ApprovalRequest
        {
            Kind = "edit_file",
            Path = "one.cs",
            UnifiedDiff = "second edit",
        });

        presenter.Files.Should().HaveCount(2);
        presenter.Files[0].Path.Should().Be("one.cs", "most recent edit floats to the top");
        presenter.Files[0].Diff.Should().Be("second edit");
        presenter.Files[0].Edits.Should().Be(2);
        presenter.Files[1].Path.Should().Be("two.cs");
        changes.Should().Be(3);
    }

    [Fact]
    public void Workspace_mutations_go_through_the_ui_marshal()
    {
        var marshalled = 0;
        var presenter = new WorkspacePresenter
        {
            UiInvoke = action =>
            {
                marshalled++;
                action();
            },
        };

        presenter.OnEditApplied(Request());
        presenter.Clear();

        marshalled.Should().Be(2);
        presenter.Files.Should().BeEmpty();
    }
}
