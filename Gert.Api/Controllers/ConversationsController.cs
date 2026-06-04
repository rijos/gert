using Gert.Api.Contracts;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Service;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// Project-scoped conversation CRUD (rest-api.md § conversations). Thin: it
/// delegates to <see cref="IConversationService"/> via the <see cref="IGertServices"/>
/// hub. The user is implicit (from the token) — there is no <c>userId</c> in the
/// path; <c>pid</c> resolves only within the caller's own folder (configuration.md
/// § 2.5). Covered by the fallback authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}/conversations")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IGertServices _services;

    public ConversationsController(IGertServices services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>List the project's conversations (lazily provisions the folder on first touch).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Conversation>>> List(
        string pid,
        CancellationToken cancellationToken)
    {
        var conversations = await _services.Conversations
            .ListAsync(pid, cancellationToken)
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
        var created = await _services.Conversations
            .CreateAsync(pid, request, cancellationToken)
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
        var thread = await _services.Conversations
            .GetAsync(pid, id, cancellationToken)
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
        var updated = await _services.Conversations
            .UpdateAsync(pid, id, request, cancellationToken)
            .ConfigureAwait(false);

        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>Delete a conversation and cascade its messages/tool calls/citations/artifacts.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(
        string pid,
        string id,
        CancellationToken cancellationToken)
    {
        var deleted = await _services.Conversations
            .DeleteAsync(pid, id, cancellationToken)
            .ConfigureAwait(false);

        return deleted ? NoContent() : NotFound();
    }
}
