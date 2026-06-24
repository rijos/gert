namespace Gert.TurnControl;

/// <summary>
/// The user-bound address of a turn's control channel (chat-and-tools.md section detached turns).
/// <see cref="UserKey"/> is the token-derived <c>sha256(iss + "\n" + sub)</c> folder key - NEVER a
/// request-supplied value - and is the security anchor: a cancel/answer addressed with one user's key
/// can never reach another user's turn even if the conversation id is known (a conversation id alone is
/// a random GUID, not an authorization boundary). <see cref="ProjectId"/> + <see cref="ConversationId"/>
/// locate the turn under that user. The runner subscribes under this triple; the endpoints publish to it.
/// </summary>
public readonly record struct ControlScope(string UserKey, string ProjectId, string ConversationId);
