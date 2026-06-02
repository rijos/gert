using Gert.Model.Dtos;
using Gert.Model.Events;

namespace Gert.Service.Chat;

/// <summary>
/// The chat orchestrator (chat-and-tools.md § tool loop). Streams a turn as a
/// sequence of <see cref="ChatEvent"/>s — <c>message_start → (tool_call →
/// tool_result)* → delta* → citation* → artifact* → message_end</c> — and
/// persists everything to the project's <c>chat.db</c> as the stream completes.
/// Transport-agnostic: the Api renders the stream as SSE, the Console prints it.
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Send a user message to a conversation and stream the assistant turn.
    /// Scoped to the caller's <c>(iss, sub)</c> (via <see cref="IUserContext"/>)
    /// and the given project/conversation.
    /// </summary>
    /// <param name="pid">Project id — a UUID or the literal <c>default</c>.</param>
    /// <param name="conversationId">Target conversation id.</param>
    /// <param name="request">The message body (content, optional model/tools).</param>
    IAsyncEnumerable<ChatEvent> SendMessageAsync(
        string pid,
        string conversationId,
        SendMessageRequest request,
        CancellationToken cancellationToken = default);
}
