using Gert.Api.Validation;
using Gert.Model.Dtos;
using Gert.Service;
using Gert.Service.Chat;
using Gert.Service.Validation;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// <c>POST /api/projects/{pid}/conversations/{id}/messages</c> - accept a user
/// message and start a detached turn (rest-api.md section sending a message,
/// chat-and-tools.md section detached turns). The controller is transport-only:
/// <see cref="ITurnPlanner"/> validates and persists (a thrown
/// <see cref="Gert.Service.Validation.ValidationException"/> -> 400, a
/// <see cref="TurnInProgressException"/> -> 409, both via the exception
/// handlers), the job is queued for the background worker, and the client
/// receives <b>202</b> with the ids + the cursor to subscribe from - delivery
/// happens on the WS/SSE/range endpoints, and generation survives the client
/// disconnecting. Also hosts the inverse, <c>POST .../cancel</c> (section stop
/// generation). Covered by the fallback authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}/conversations/{id}/messages")]
public sealed class MessagesController : ControllerBase
{
    private readonly ITurnPlanner _planner;
    private readonly ITurnQueue _queue;
    private readonly ITurnCancellation _cancellation;
    private readonly ITurnQuestions _questions;
    private readonly IValidationProvider _validation;
    private readonly IUserContext _user;

    public MessagesController(
        ITurnPlanner planner,
        ITurnQueue queue,
        ITurnCancellation cancellation,
        ITurnQuestions questions,
        IValidationProvider validation,
        IUserContext user)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        _questions = questions ?? throw new ArgumentNullException(nameof(questions));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
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
    /// <c>POST /api/projects/{pid}/conversations/{id}/cancel</c> - stop the
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

    /// <summary>
    /// <c>POST /api/projects/{pid}/conversations/{id}/answer</c> - deliver the
    /// user's answer to the in-flight turn's pending <c>ask_user</c> question
    /// (rest-api.md section answer a question). Mirrors <see cref="Cancel"/>: the key
    /// carries the caller's own iss/sub, so a foreign conversation id can never
    /// address another tenant's question (404, indistinguishable from
    /// none-pending). <b>202</b> when the waiting tool received the answer,
    /// <b>404</b> when nothing is pending / the question id is stale (the
    /// question may have just timed out - the client marks its card expired),
    /// <b>400</b> when a closed question's answer names no offered option.
    /// </summary>
    [HttpPost("~/api/projects/{pid}/conversations/{id}/answer")]
    public IActionResult Answer(string pid, string id, [FromBody] AnswerRequest request)
    {
        RouteParams.RequireValidProjectId(pid);

        // Fail-closed body validation (principle #6): same 400 ProblemDetails
        // path as the service-layer validators, via ValidationExceptionHandler.
        var result = _validation.Validate(request);
        if (!result.IsValid)
        {
            throw new ValidationException(result);
        }

        var outcome = _questions.Answer(new TurnKey(_user.Iss, _user.Sub, pid, id), request);
        return outcome switch
        {
            AnswerOutcome.Delivered => Accepted(),
            AnswerOutcome.InvalidOption => Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid answer",
                detail: "The answer is not among the offered options."),
            _ => NotFound(),
        };
    }
}
