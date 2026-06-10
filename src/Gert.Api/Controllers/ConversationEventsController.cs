using Gert.Api.Sse;
using Gert.Api.Validation;
using Gert.Model.Dtos;
using Gert.Service.Chat;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// The delivery read side of the detached turn pipeline (rest-api.md § receiving
/// a turn): the paginated event log (catch-up / resume / poll fallback) and the
/// SSE live stream. Both are thin views over the same core —
/// <see cref="IConversationReader"/> (DB truth) and
/// <see cref="IConversationStreamer"/> (the gap/dup-free splice). Covered by the
/// fallback authenticated-user policy; the WS sibling lives in
/// <c>WebSockets/ChatWebSocketEndpoint</c> (it needs the upgrade handshake).
/// </summary>
[ApiController]
[Route("api/projects/{pid}/conversations/{id}")]
public sealed class ConversationEventsController : ControllerBase
{
    private readonly IConversationReader _reader;
    private readonly IConversationStreamer _streamer;

    public ConversationEventsController(IConversationReader reader, IConversationStreamer streamer)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _streamer = streamer ?? throw new ArgumentNullException(nameof(streamer));
    }

    /// <summary>
    /// One page of the conversation's event log: events with
    /// <c>seq &gt; after</c>, ascending, at most <c>limit</c>. Always served from
    /// the database — correct across instances and restarts, and the polling
    /// fallback when neither WS nor SSE is available.
    /// </summary>
    [HttpGet("events")]
    public async Task<ActionResult<ConversationRange>> Range(
        string pid,
        string id,
        [FromQuery] long after = 0,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        RouteParams.RequireValidProjectId(pid);

        var range = await _reader
            .ReadRangeAsync(pid, id, after, limit, cancellationToken)
            .ConfigureAwait(false);
        return Ok(range);
    }

    /// <summary>
    /// Live SSE stream from a cursor: DB catch-up first, then live events as the
    /// worker produces them (the splice). Runs until the client disconnects —
    /// detached generation continues regardless. Frames carry <c>id: seq</c> so
    /// the client can resume with <c>?after</c> after a drop.
    /// </summary>
    [HttpGet("stream")]
    public Task Stream(
        string pid,
        string id,
        [FromQuery] long after = 0,
        CancellationToken cancellationToken = default)
    {
        RouteParams.RequireValidProjectId(pid);

        var events = _streamer.StreamAsync(pid, id, after, cancellationToken);
        return TurnSseWriter.WriteAsync(Response, events, cancellationToken);
    }
}
