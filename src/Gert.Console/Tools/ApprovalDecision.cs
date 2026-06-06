namespace Gert.Console.Tools;

/// <summary>The user's verdict on a gated tool action (U16 approval flow).</summary>
public enum ApprovalDecision
{
    /// <summary>Apply the edit / run the command.</summary>
    Approve,

    /// <summary>Reject it — the tool returns a failure the model can read and adapt to.</summary>
    Deny,
}
