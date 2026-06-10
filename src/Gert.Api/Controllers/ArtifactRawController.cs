using System.Security.Claims;
using Gert.Api.Security;
using Gert.Model;
using Gert.Service.Documents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// Serves a rendered HTML artifact as a standalone document on the <b>sandbox
/// origin</b> (security F3, served-document hardening). Framed by the SPA via
/// <c>&lt;iframe src&gt;</c> from a separate origin, so it gets:
/// <list type="bullet">
///   <item>its <b>own, non-inherited CSP</b> — <c>script-src 'unsafe-inline'</c>
///   restores demo fidelity while <c>connect-src 'none'</c> / <c>form-action 'none'</c>
///   slam the egress + phishing doors;</item>
///   <item><c>sandbox allow-scripts</c> in that CSP → an <b>opaque origin</b>, so the
///   document can't reach the app's token/DOM/cookies even though it's network-served.</item>
/// </list>
/// <para>
/// <b>Anonymous by design:</b> a cross-origin iframe navigation can't carry the
/// in-memory bearer (F2), so authorization rides a short-lived HMAC ticket minted
/// by the authed, <c>pid</c>-scoped <c>…/artifacts/{id}/ticket</c> endpoint. No
/// valid ticket → 403; the ticket alone names which artifact may be read.
/// </para>
/// </summary>
[ApiController]
[Route("artifacts")]
[AllowAnonymous]
public sealed class ArtifactRawController : ControllerBase
{
    // The rendered document's policy: deny-by-default, inline scripts/styles for
    // fidelity (safe — the doc is an isolated, zero-egress opaque origin), no
    // network, no form posts, and self-sandbox so it stays opaque even if reached
    // directly. connect-src/frame-src/etc. fall back to default-src 'none'.
    private const string ArtifactCsp =
        "default-src 'none'; " +
        "script-src 'unsafe-inline'; " +
        "style-src 'unsafe-inline'; " +
        "img-src data: blob:; " +
        "font-src data:; " +
        "form-action 'none'; " +
        "base-uri 'none'; " +
        "sandbox allow-scripts";

    // Granular interface, not the IGertServices hub (dotnet-style-guide.md §4).
    private readonly IArtifactService _artifacts;
    private readonly ArtifactTicketService _tickets;

    public ArtifactRawController(IArtifactService artifacts, ArtifactTicketService tickets)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
    }

    /// <summary>Render the ticketed HTML artifact as a sandboxed document.</summary>
    [HttpGet("raw")]
    public async Task<IActionResult> Raw(
        [FromQuery] string? t,
        CancellationToken cancellationToken)
    {
        // The ticket is the only authority here. Bad/expired/forged → 403, never a
        // hint about whether the artifact exists.
        if (!_tickets.TryValidate(t, out var ticket))
        {
            return Forbid();
        }

        // The artifact store is keyed on iss+sub. This request carries no bearer, so
        // seed HttpContext.User from the (signed) ticket identity — HttpUserContext
        // reads claims lazily, so the scoped IArtifactService now resolves exactly
        // the ticketed user's storage, unchanged.
        HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim("sub", ticket.Sub), new Claim("iss", ticket.Iss)],
                authenticationType: "ArtifactTicket"));

        var artifact = await _artifacts
            .GetAsync(ticket.Pid, ticket.ArtifactId, cancellationToken)
            .ConfigureAwait(false);
        if (artifact is null)
        {
            return NotFound();
        }

        // Only HTML renders as a live document; everything else would be a content-
        // type confusion waiting to happen. (SVG renders client-side via srcdoc.)
        if (artifact.Kind != ArtifactKind.Html)
        {
            return BadRequest();
        }

        // The endpoint owns its headers; SecurityHeadersMiddleware steps aside when a
        // CSP is already set, so this per-document policy is the one that applies.
        Response.Headers["Content-Security-Policy"] = ArtifactCsp;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Content(artifact.Content, "text/html; charset=utf-8");
    }
}
