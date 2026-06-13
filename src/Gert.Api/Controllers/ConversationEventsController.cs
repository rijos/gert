using Gert.Api.Sse;
using Gert.Api.Validation;
using Gert.Model.Dtos;
using Gert.Service.Chat;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// The delivery read side of the detached turn pipeline (rest-api.md section receiving
/// a turn): the paginated event log (catch-up / resume / poll fallback) and the
/// SSE live stream. Both are thin views over the same core -
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
    private readonly IHostApplicationLifetime _lifetime;

    public ConversationEventsController(
        IConversationReader reader,
        IConversationStreamer streamer,
        IHostApplicationLifetime lifetime)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _streamer = streamer ?? throw new ArgumentNullException(nameof(streamer));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    }

    /// <summary>
    /// One page of the conversation's event log: events with
    /// <c>seq &gt; after</c>, ascending, at most <c>limit</c>. Always served from
    /// the database - correct across instances and restarts, and the polling
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
    /// worker produces them (the splice). Runs until the client disconnects -
    /// detached generation continues regardless. Frames carry <c>id: seq</c> so
    /// the client can resume with <c>?after</c> after a drop.
    /// </summary>
    [HttpGet("stream")]
    public async Task Stream(
        string pid,
        string id,
        [FromQuery] long after = 0,
        CancellationToken cancellationToken = default)
    {
        RouteParams.RequireValidProjectId(pid);

        // RequestAborted never fires during graceful shutdown, so an open SSE
        // stream would otherwise pin the host for the whole drain timeout -
        // link ApplicationStopping so shutdown force-disconnects the client.
        // Dropping the transport loses nothing durable: the client reconnects
        // with ?after and the splice resumes from the event log.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.ApplicationStopping);

        try
        {
            var events = _streamer.StreamAsync(pid, id, after, linked.Token);
            await TurnSseWriter.WriteAsync(Response, events, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Client gone or host stopping - normal teardown for a live stream.
        }
    }
}
