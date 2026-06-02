using Gert.Api.Sse;
using Gert.Model.Dtos;
using Gert.Service;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// <c>POST /api/projects/{pid}/conversations/{id}/messages</c> — send a user
/// message and stream the assistant turn as Server-Sent Events (rest-api.md
/// § sending a message). The controller is transport-only: it iterates the
/// <see cref="IChatService"/> event stream and renders each <see cref="Model.Events.ChatEvent"/>
/// as an SSE frame via <see cref="SseWriter"/>. Persistence happens inside the
/// service as the stream completes. Covered by the fallback authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}/conversations/{id}/messages")]
public sealed class MessagesController : ControllerBase
{
    private readonly IGertServices _services;

    public MessagesController(IGertServices services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>
    /// Stream the assistant turn as <c>text/event-stream</c>, driving the two
    /// stateless service phases within this one request: phase 1
    /// (<c>StartTurnAsync</c>) validates + persists the user message + builds the
    /// in-memory turn — a thrown <see cref="Gert.Service.Validation.ValidationException"/> becomes
    /// a 400 ProblemDetails (via the exception handler) <b>before</b> the stream
    /// opens; phase 2 (<c>RunAsync</c>) streams the prepared turn to SSE.
    /// </summary>
    [HttpPost]
    public async Task Post(
        string pid,
        string id,
        [FromBody] SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        // Phase 1 runs before any SSE bytes — invalid input throws and is mapped to
        // 400 by the exception handler, never an in-stream error frame.
        var turn = await _services.Chat.StartTurnAsync(pid, id, request, cancellationToken)
            .ConfigureAwait(false);

        // Phase 2: stream the prepared turn.
        var events = _services.Chat.RunAsync(turn, cancellationToken);
        await SseWriter.WriteAsync(Response, events, cancellationToken).ConfigureAwait(false);
    }
}
