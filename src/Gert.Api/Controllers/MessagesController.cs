using Gert.Agent;
using Gert.Api.Validation;
using Gert.Model.Dtos;
using Gert.Service;
using Gert.Service.Chat;
using Gert.Storage;
using Gert.TurnControl;
using Gert.Validation;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// <c>POST /api/projects/{pid}/conversations/{id}/messages</c> - accept a user
/// message and start a detached turn (rest-api.md section sending a message,
/// chat-and-tools.md section detached turns). The controller is transport-only:
/// <see cref="ITurnPlanner"/> validates and persists (a thrown
/// <see cref="Gert.Validation.ValidationException"/> -> 400, a
/// <see cref="TurnInProgressException"/> -> 409, both via the exception
/// handlers), the job is queued for the background worker, and the client
/// receives <b>202</b> with the ids + the cursor to subscribe from - delivery
/// happens on the SSE/range endpoints, and generation survives the client
/// disconnecting. Also hosts the inverse, <c>POST .../cancel</c> and <c>.../answer</c>
/// (sections stop generation / answer a question): both go to the <see cref="ITurnControlBus"/>
/// control plane addressed by a <see cref="ControlScope"/> whose user key is derived from the
/// caller's validated token (iss/sub) - so a foreign conversation id can never reach another
/// tenant's turn (it addresses no live subscription -> 404/no-op). Covered by the fallback
/// authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}/conversations/{id}/messages")]
public sealed class MessagesController : ControllerBase
{
    private readonly ITurnPlanner _planner;
    private readonly ITurnQueue _queue;
    private readonly ITurnControlBus _control;
    private readonly IValidationProvider _validation;
    private readonly IUserContext _user;

    public MessagesController(
        ITurnPlanner planner,
        ITurnQueue queue,
        ITurnControlBus control,
        IValidationProvider validation,
        IUserContext user)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _control = control ?? throw new ArgumentNullException(nameof(control));
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

        // Prove at the boundary (principle #6): an invalid body throws -> 400.
        var job = await _planner.PlanAsync(pid, id, _validation.Prove(request), cancellationToken)
            .ConfigureAwait(false);
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
    /// in-flight turn by publishing a cancel to its control channel (rest-api.md
    /// section stop generation). Fire-and-forget and idempotent: always <b>202</b>
    /// (a cancel for an idle/unknown/foreign conversation simply reaches no live
    /// subscription and is dropped). The runner unwinds reactively when the signal
    /// trips its linked token; a cancel published while the turn is still queued is
    /// caught when the runner subscribes (the bus retains it against the turn's plan
    /// instant). The terminal <c>cancelled</c> event arrives on the normal transports.
    /// </summary>
    [HttpPost("~/api/projects/{pid}/conversations/{id}/cancel")]
    public async Task<IActionResult> Cancel(string pid, string id, CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        // The conversation id is only a scope key here (never a path), so no shape guard is needed:
        // an unknown/malformed id simply addresses no live subscription and the publish is a no-op.
        await _control.PublishCancelAsync(Scope(pid, id), cancellationToken).ConfigureAwait(false);
        return Accepted();
    }

    /// <summary>
    /// <c>POST /api/projects/{pid}/conversations/{id}/answer</c> - deliver the
    /// user's answer to the in-flight turn's pending <c>ask_user</c> question
    /// (rest-api.md section answer a question) via the control bus, which validates
    /// the body against the open question and routes it to the waiting tool. The
    /// scope's user key comes from the caller's token, so a foreign conversation id
    /// addresses no tenant's question (404, indistinguishable from none-pending).
    /// <b>202</b> when the answer was delivered, <b>404</b> when nothing is pending /
    /// the question id is stale (the question may have just timed out - the client
    /// marks its card expired), <b>400</b> when a closed question's answer names no
    /// offered option (or the count mismatches).
    /// </summary>
    [HttpPost("~/api/projects/{pid}/conversations/{id}/answer")]
    public async Task<IActionResult> Answer(
        string pid,
        string id,
        [FromBody] AnswerRequest request,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        // Prove at the boundary (principle #6): an invalid body throws -> 400.
        var dto = _validation.Prove(request).Value;

        var outcome = await _control
            .SubmitAnswerAsync(Scope(pid, id), dto.QuestionId, dto.Answers, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            AnswerOutcome.Accepted => Accepted(),
            AnswerOutcome.Invalid => Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid answer",
                detail: "The answer is not among the offered options."),
            _ => NotFound(),
        };
    }

    /// <summary>
    /// The control-plane address: the caller's token-derived user key (<c>sha256(iss+sub)</c>, the
    /// security anchor - never a request value) joined with the route project + conversation. A
    /// request can only ever address its own user's turns.
    /// </summary>
    private ControlScope Scope(string pid, string id) =>
        new(StorageKeys.UserKey(_user.Iss, _user.Sub), pid, id);
}
