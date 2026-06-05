using Gert.Model.Dtos;
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
/// disconnecting. Covered by the fallback authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}/conversations/{id}/messages")]
public sealed class MessagesController : ControllerBase
{
    private readonly ITurnPlanner _planner;
    private readonly ITurnQueue _queue;

    public MessagesController(ITurnPlanner planner, ITurnQueue queue)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    /// <summary>Plan (validate + persist) and enqueue the turn; 202 with the subscribe cursor.</summary>
    [HttpPost]
    public async Task<ActionResult<TurnAccepted>> Post(
        string pid,
        string id,
        [FromBody] SendMessageRequest request,
        CancellationToken cancellationToken)
    {
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
}
