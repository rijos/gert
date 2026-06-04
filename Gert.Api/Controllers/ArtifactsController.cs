using Gert.Api.Validation;
using Gert.Model.Chat;
using Gert.Service;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// Chat artifacts (rest-api.md § artifacts) — the canvas tabs produced during chat
/// and stored in the project's <c>chat.db</c>. Two reads: list a conversation's
/// artifacts, and fetch one artifact's raw content. Both validate <c>{pid}</c>
/// first. Covered by the fallback authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}")]
public sealed class ArtifactsController : ControllerBase
{
    private readonly IGertServices _services;

    public ArtifactsController(IGertServices services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>List a conversation's artifacts for the canvas tab strip.</summary>
    [HttpGet("conversations/{conversationId}/artifacts")]
    public async Task<ActionResult<IReadOnlyList<Artifact>>> List(
        string pid,
        string conversationId,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var artifacts = await _services.Artifacts
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

        var artifact = await _services.Artifacts.GetAsync(pid, id, cancellationToken).ConfigureAwait(false);
        return artifact is null ? NotFound() : Ok(artifact);
    }
}
