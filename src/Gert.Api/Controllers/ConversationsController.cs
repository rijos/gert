using Gert.Api.Contracts;
using Gert.Api.Validation;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Service.Chat;
using Gert.Service.Conversations;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// Project-scoped conversation CRUD (rest-api.md § conversations). Thin: it
/// delegates to the granular <see cref="IConversationService"/>
/// (dotnet-style-guide.md §4). The user is implicit (from the token) — there is no <c>userId</c> in the
/// path; <c>pid</c> resolves only within the caller's own folder (configuration.md
/// § 2.5). Every action validates <c>{pid}</c> first. Covered by the fallback
/// authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}/conversations")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IConversationService _conversations;
    private readonly IConversationReader _reader;

    public ConversationsController(IConversationService conversations, IConversationReader reader)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <summary>List the project's conversations (lazily provisions the folder on first touch).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Conversation>>> List(
        string pid,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var conversations = await _conversations
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
        RouteParams.RequireValidProjectId(pid);

        var created = await _conversations
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
        RouteParams.RequireValidProjectId(pid);

        // Through the reader, not the CRUD service: it applies the orphan rule, so
        // an abandoned streaming row reads as error (chat-and-tools.md § detached turns).
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
        RouteParams.RequireValidProjectId(pid);

        var deleted = await _conversations
            .DeleteAsync(pid, id, cancellationToken)
            .ConfigureAwait(false);

        return deleted ? NoContent() : NotFound();
    }
}
