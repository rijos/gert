using Gert.Api.Contracts;
using Gert.Api.Validation;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Service.Chat;
using Gert.Service.Conversations;
using Gert.Service.Validation;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// Project-scoped conversation CRUD (rest-api.md section conversations). Thin: it
/// delegates to the granular <see cref="IConversationService"/>
/// (dotnet-style-guide.md section 4). The user is implicit (from the token) - there is no <c>userId</c> in the
/// path; <c>pid</c> resolves only within the caller's own folder (configuration.md
/// section 2.5). Every action validates <c>{pid}</c> first. Covered by the fallback
/// authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}/conversations")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IConversationService _conversations;
    private readonly IConversationReader _reader;
    private readonly IValidationProvider _validation;

    public ConversationsController(
        IConversationService conversations,
        IConversationReader reader,
        IValidationProvider validation)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
    }

    /// <summary>
    /// List the project's conversations (lazily provisions the folder on first
    /// touch). <c>q</c> filters by title; <c>limit</c>/<c>offset</c> page for
    /// the search overlay's infinite scroll (limit 0 = all, capped at 100).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Conversation>>> List(
        string pid,
        [FromQuery] string? q = null,
        [FromQuery] int limit = 0,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        RouteParams.RequireValidProjectId(pid);

        var conversations = await _conversations
            .ListAsync(pid, q, limit, Math.Max(offset, 0), cancellationToken)
            .ConfigureAwait(false);
        return Ok(conversations);
    }

    /// <summary>Create a conversation; unset fields inherit project/user defaults.</summary>
    [HttpPost]
    public async Task<ActionResult<Conversation>> Create(
        string pid,
        [FromBody] CreateConversationRequest request,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var created = await _conversations
            .CreateAsync(pid, _validation.Prove(request), cancellationToken)
            .ConfigureAwait(false);

        return CreatedAtAction(
            nameof(Get),
            new { pid, id = created.Id },
            created);
    }

    /// <summary>Load a full thread (messages + citations + artifacts), flattened for the SPA.</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<ThreadResponse>> Get(
        string pid,
        string id,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        // Through the reader, not the CRUD service: it applies the orphan rule, so
        // an abandoned streaming row reads as error (chat-and-tools.md section detached turns).
        var thread = await _reader
            .GetThreadAsync(pid, id, cancellationToken)
            .ConfigureAwait(false);

        return thread is null ? NotFound() : Ok(ThreadResponse.From(thread));
    }

    /// <summary>Apply a partial update (rename / switch model / toggle tools / archive).</summary>
    [HttpPatch("{id}")]
    public async Task<ActionResult<Conversation>> Update(
        string pid,
        string id,
        [FromBody] UpdateConversationRequest request,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var updated = await _conversations
            .UpdateAsync(pid, id, _validation.Prove(request), cancellationToken)
            .ConfigureAwait(false);

        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>
    /// Move the conversation to another of the caller's projects. 404 when it
    /// doesn't exist, 409 while a turn is streaming (the runner owns the source
    /// rows), 400 when the target already holds it.
    /// </summary>
    [HttpPost("{id}/move")]
    public async Task<ActionResult<Conversation>> Move(
        string pid,
        string id,
        [FromBody] MoveConversationRequest request,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var moved = await _conversations
            .MoveAsync(pid, id, _validation.Prove(request), cancellationToken)
            .ConfigureAwait(false);

        return moved is null ? NotFound() : Ok(moved);
    }

    /// <summary>Delete a conversation and cascade its messages/tool calls/citations/artifacts.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(
        string pid,
        string id,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var deleted = await _conversations
            .DeleteAsync(pid, id, cancellationToken)
            .ConfigureAwait(false);

        return deleted ? NoContent() : NotFound();
    }
}
