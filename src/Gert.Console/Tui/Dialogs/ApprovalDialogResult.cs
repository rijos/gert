using Gert.Console.Tools;

namespace Gert.Console.Tui.Dialogs;

/// <summary>The approval dialog's outcome: the verdict + the "stop asking" flag.</summary>
public sealed record ApprovalDialogResult(ApprovalDecision Decision, bool AutoApproveAll);
