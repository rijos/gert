using Gert.Api.Security;
using Gert.Api.Validation;
using Gert.Model.Chat;
using Gert.Service;
using Gert.Service.Documents;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// Chat artifacts (rest-api.md section artifacts) - the canvas tabs produced during chat
/// and stored in the project's <c>chat.db</c>. Reads: list a conversation's
/// artifacts, fetch one artifact's raw content, and mint a capability ticket to
/// render an HTML artifact from the sandbox origin (security F3). All validate
/// <c>{pid}</c> first and are covered by the fallback authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}")]
public sealed class ArtifactsController : ControllerBase
{
    // Granular interface, not the IGertServices hub (dotnet-style-guide.md section 4).
    private readonly IArtifactService _artifacts;
    private readonly ArtifactTicketService _tickets;
    private readonly IUserContext _user;

    public ArtifactsController(
        IArtifactService artifacts,
        ArtifactTicketService tickets,
        IUserContext user)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <summary>List a conversation's artifacts for the canvas tab strip.</summary>
    [HttpGet("conversations/{conversationId}/artifacts")]
    public async Task<ActionResult<IReadOnlyList<Artifact>>> List(
        string pid,
        string conversationId,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var artifacts = await _artifacts
            .ListAsync(pid, conversationId, cancellationToken)
            .ConfigureAwait(false);
        return Ok(artifacts);
    }

    /// <summary>Get one artifact's raw content (download / "Source" view).</summary>
    [HttpGet("artifacts/{id}")]
    public async Task<ActionResult<Artifact>> Get(
        string pid,
        string id,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var artifact = await _artifacts.GetAsync(pid, id, cancellationToken).ConfigureAwait(false);
        return artifact is null ? NotFound() : Ok(artifact);
    }

    /// <summary>
    /// Mint a short-lived, signed URL that renders this artifact from the sandbox
    /// origin (F3). The ticket is issued <b>only after</b> the authed, <c>pid</c>-scoped
    /// lookup confirms the artifact exists under the caller's project, so the
    /// capability can never name another user's artifact. The cross-origin
    /// <c>&lt;iframe src&gt;</c> can't carry the in-memory bearer (F2), so the raw
    /// endpoint trusts this ticket instead.
    /// </summary>
    [HttpGet("artifacts/{id}/ticket")]
    public async Task<ActionResult<ArtifactTicketResponse>> Ticket(
        string pid,
        string id,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var artifact = await _artifacts.GetAsync(pid, id, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            return NotFound();
        }

        // Bind the caller's identity into the ticket: the raw endpoint is anonymous
        // (no bearer over a cross-origin frame), so it resolves the requester's
        // per-user artifact store from iss+sub carried here.
        var ticket = _tickets.Mint(_user.Iss, _user.Sub, pid, id);
        // Absolute URL on the configured artifact origin, or origin-relative when no
        // separate origin is configured (same-origin fallback). Either way the SPA
        // frames exactly what it's handed and never needs to know the origin.
        var url = $"{_tickets.Origin}/artifacts/raw?t={Uri.EscapeDataString(ticket)}";
        return Ok(new ArtifactTicketResponse(url));
    }
}
