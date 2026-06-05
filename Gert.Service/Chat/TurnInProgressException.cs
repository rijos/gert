namespace Gert.Service.Chat;

/// <summary>
/// A new turn was requested while the conversation's latest assistant row is
/// still <c>streaming</c> (chat-and-tools.md § detached turns). Serializing
/// turns per conversation is what keeps history correct (turn N+1 must see turn
/// N's answer) and upholds the seq single-writer invariant. The host maps this
/// to <c>409 Conflict</c>; the SPA already disables the composer while
/// streaming, so a user only hits this from a second tab or a raced double-send.
/// </summary>
public sealed class TurnInProgressException : InvalidOperationException
{
    public TurnInProgressException(string conversationId)
        : base($"A turn is already in progress for conversation '{conversationId}'.")
    {
        ConversationId = conversationId;
    }

    public string ConversationId { get; }
}
