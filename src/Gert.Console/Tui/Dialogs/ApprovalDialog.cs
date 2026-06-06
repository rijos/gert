using Gert.Console.Tools;
using Gert.Console.Tui.Views;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Gert.Console.Tui.Dialogs;

/// <summary>
/// The gated-tool approval dialog (U16): shows the unified diff (writes) or
/// the command line (shell) and asks Approve / Deny / Approve-all. Esc means
/// Deny — never silently applies. Runs a nested modal loop on the UI thread;
/// a turn stop (the token) closes it and returns null.
/// </summary>
public static class ApprovalDialog
{
    /// <summary>Show the dialog; null only when the turn was cancelled under it.</summary>
    public static ApprovalDialogResult? Show(
        IApplication application,
        ApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(request);

        ApprovalDialogResult? result = null;

        using var dialog = new Dialog
        {
            Title = request.Command is null
                ? $"{request.Kind}: {request.Path}"
                : "run_shell",
            Width = Dim.Percent(80),
            Height = Dim.Percent(70),
        };

        if (request.Command is { } command)
        {
            dialog.Add(new Label
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(1),
                Text = $"$ {command}",
            });
        }
        else
        {
            var diff = new DiffView
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(1),
            };
            diff.SetDiff(request.UnifiedDiff);
            dialog.Add(diff);
        }

        void Close(ApprovalDialogResult? outcome)
        {
            result = outcome;
            application.RequestStop(dialog);
        }

        var approve = new Button { Title = "_Approve", IsDefault = true };
        approve.Accepting += (_, e) =>
        {
            e.Handled = true;
            Close(new ApprovalDialogResult(ApprovalDecision.Approve, AutoApproveAll: false));
        };

        var deny = new Button { Title = "_Deny" };
        deny.Accepting += (_, e) =>
        {
            e.Handled = true;
            Close(new ApprovalDialogResult(ApprovalDecision.Deny, AutoApproveAll: false));
        };

        var approveAll = new Button { Title = "Approve a_ll" };
        approveAll.Accepting += (_, e) =>
        {
            e.Handled = true;
            Close(new ApprovalDialogResult(ApprovalDecision.Approve, AutoApproveAll: true));
        };

        dialog.AddButton(approve);
        dialog.AddButton(deny);
        dialog.AddButton(approveAll);

        // A turn stop while the dialog is open closes it as "no answer".
        using var registration = cancellationToken.Register(
            () => application.Invoke(() => application.RequestStop(dialog)));

        application.Run(dialog);

        // Esc / window close without a verdict = Deny (never silently apply) —
        // unless the close came from the cancellation above.
        if (result is null && !cancellationToken.IsCancellationRequested)
        {
            return new ApprovalDialogResult(ApprovalDecision.Deny, AutoApproveAll: false);
        }

        return result;
    }
}
