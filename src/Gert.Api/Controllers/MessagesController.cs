using Gert.Api.Validation;
using Gert.Model.Dtos;
using Gert.Service;
using Gert.Service.Chat;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// <c>POST /api/projects/{pid}/conversations/{id}/messages</c> — accept a user
/// message and start a detached turn (rest-api.md § sending a message,
/// chat-and-tools.md § detached turns). The controller is transport-only:
/// <see cref="ITurnPlanner"/> validates and persists (a thrown
/// <see cref="Gert.Service.Validation.ValidationException"/> → 400, a
/// <see cref="TurnInProgressException"/> → 409, both via the exception
/// handlers), the job is queued for the background worker, and the client
/// receives <b>202</b> with the ids + the cursor to subscribe from — delivery
/// happens on the WS/SSE/range endpoints, and generation survives the client
/// disconnecting. Also hosts the inverse, <c>POST …/cancel</c> (§ stop
/// generation). Covered by the fallback authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}/conversations/{id}/messages")]
public sealed class MessagesController : ControllerBase
{
    private readonly ITurnPlanner _planner;
    private readonly ITurnQueue _queue;
    private readonly ITurnCancellation _cancellation;
    private readonly IUserContext _user;

    public MessagesController(
        ITurnPlanner planner,
        ITurnQueue queue,
        ITurnCancellation cancellation,
        IUserContext user)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <summary>Plan (validate + persist) and enqueue the turn; 202 with the subscribe cursor.</summary>
    [HttpPost]
    public async Task<ActionResult<TurnAccepted>> Post(
        string pid,
        string id,
        [FromBody] SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var job = await _planner.PlanAsync(pid, id, request, cancellationToken).ConfigureAwait(false);
        await _queue.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);

        return Accepted(new TurnAccepted
        {
            ConversationId = job.ConversationId,
            UserMessageId = job.UserMessageId,
            AssistantMessageId = job.AssistantMessageId,
            Seq = job.AssistantSeq,
        });
    }

    /// <summary>
    /// <c>POST /api/projects/{pid}/conversations/{id}/cancel</c> — stop the
    /// in-flight turn. The key carries the caller's own iss/sub, so a foreign
    /// conversation id can never address another tenant's turn. Idempotent:
    /// <b>202</b> when a live turn was signalled, <b>204</b> when there was
    /// nothing to stop (a tombstone covers the still-queued race). The terminal
    /// <c>cancelled</c> event arrives on the normal delivery transports.
    /// </summary>
    [HttpPost("~/api/projects/{pid}/conversations/{id}/cancel")]
    public IActionResult Cancel(string pid, string id)
    {
        RouteParams.RequireValidProjectId(pid);

        return _cancellation.Cancel(new TurnKey(_user.Iss, _user.Sub, pid, id))
            ? Accepted()
            : NoContent();
    }
}
