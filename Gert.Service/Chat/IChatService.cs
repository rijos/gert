using Gert.Model.Dtos;
using Gert.Model.Events;

namespace Gert.Service.Chat;

/// <summary>
/// The chat orchestrator (chat-and-tools.md § tool loop), split into two explicit,
/// <b>stateless</b> phases the host drives within a single HTTP request:
/// <list type="number">
///   <item>
///     <see cref="StartTurnAsync"/> — "request properly": validate the input,
///     persist the user message, load prior turns, and build the in-memory
///     <see cref="ChatTurn"/>. Invalid input throws
///     <see cref="Validation.ValidationException"/> (host maps to 400) <b>before</b>
///     any model/stream is opened.
///   </item>
///   <item>
///     <see cref="RunAsync"/> — stream the assistant response for a prepared turn
///     (<c>message_start → delta* → message_end</c>) and persist the assistant
///     message as the stream completes. A model failure surfaces as an in-stream
///     <see cref="ErrorEvent"/>.
///   </item>
/// </list>
/// There is no <c>turnId</c> and no server-side turn registry: the <see cref="ChatTurn"/>
/// carries everything phase 2 needs, so GERT runs safely as multiple instances
/// (decisions §4 / review #10). Phase 2 stays transport-agnostic — the Api renders
/// the stream as SSE, the Console prints it.
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Phase 1: validate the request, persist the user message, load prior turns,
    /// and return the in-memory <see cref="ChatTurn"/> phase 2 will stream. Scoped
    /// to the caller's <c>(iss, sub)</c> (via <see cref="IUserContext"/>) and the
    /// given project/conversation. Throws <see cref="Validation.ValidationException"/>
    /// on invalid input, before any disk/model touch.
    /// </summary>
    /// <param name="pid">Project id — a UUID or the literal <c>default</c>.</param>
    /// <param name="conversationId">Target conversation id.</param>
    /// <param name="request">The message body (content, optional model/tools).</param>
    Task<ChatTurn> StartTurnAsync(
        string pid,
        string conversationId,
        SendMessageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 2: stream the assistant response for a prepared <paramref name="turn"/>
    /// and persist the assistant message as the stream completes. A model exception
    /// becomes an in-stream <see cref="ErrorEvent"/> (no assistant persisted).
    /// </summary>
    IAsyncEnumerable<ChatEvent> RunAsync(ChatTurn turn, CancellationToken cancellationToken = default);
}
