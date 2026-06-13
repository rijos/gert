using Gert.Model.Chat;

namespace Gert.Service.Chat;

/// <summary>
/// The one home of the <b>orphan rule</b> (chat-and-tools.md section detached turns):
/// the turn queue is in-memory, so a crashed worker / process restart leaves
/// assistant rows stuck at <see cref="MessageStatus.Streaming"/> forever. Rather
/// than a startup reconciliation pass (which would need cross-instance
/// coordination), every READER maps a streaming row older than the max turn
/// duration to <see cref="MessageStatus.Error"/>. Stateless and
/// multi-instance-safe. Both the thread/range read side and the planner's
/// "turn in progress" (409) check MUST go through this.
///
/// <para>
/// The shared-anchor invariant: the horizon ages the row from its
/// <c>CreatedAt</c> - the PLAN instant - and <see cref="TurnRunner"/> caps its
/// own lifetime at the budget remaining from the very same instant
/// (<see cref="TurnJob.PlannedAt"/>, one clock read in the planner). A running
/// turn therefore always self-cancels at or before the moment this rule starts
/// reporting its row as <see cref="MessageStatus.Error"/> - a queue wait can
/// never open a window where a healthy turn reads as dead and the 409 gate
/// reopens against incomplete history.
/// </para>
/// </summary>
public static class MessageStatusRules
{
    /// <summary>The status a reader should report for a message row.</summary>
    public static MessageStatus Effective(Message message, DateTimeOffset now, TimeSpan maxTurnDuration)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.Status == MessageStatus.Streaming && now - message.CreatedAt > maxTurnDuration
            ? MessageStatus.Error
            : message.Status;
    }

    /// <summary>True when this row should block a new turn (the 409 rule).</summary>
    public static bool IsTurnInProgress(Message message, DateTimeOffset now, TimeSpan maxTurnDuration) =>
        Effective(message, now, maxTurnDuration) == MessageStatus.Streaming;
}
