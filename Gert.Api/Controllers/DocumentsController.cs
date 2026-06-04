using Gert.Api.Contracts;
using Gert.Api.Validation;
using Gert.Service;
using Gert.Service.Documents;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// Project-scoped RAG documents (rest-api.md § documents). Upload accepts a
/// <c>multipart/form-data</c> file, hands it to <see cref="IDocumentService"/> as a
/// transport-agnostic <see cref="DocumentUpload"/>, and returns <c>202 Accepted</c>
/// immediately — ingestion runs on the background worker, and the client polls
/// <see cref="Get"/> for the <c>processing → ready/failed</c> transition. Responses
/// carry the <b>decoded</b> original filename (stored base64; see
/// <see cref="DocumentResponse"/>). Every action validates <c>{pid}</c> first.
/// Covered by the fallback authenticated-user policy.
/// </summary>
[ApiController]
[Route("api/projects/{pid}/documents")]
public sealed class DocumentsController : ControllerBase
{
    private readonly IGertServices _services;

    public DocumentsController(IGertServices services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>List the project's documents (decoded name, size, chunk_count, status, error).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DocumentResponse>>> List(
        string pid,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var documents = await _services.Documents.ListAsync(pid, cancellationToken).ConfigureAwait(false);
        return Ok(documents.Select(DocumentResponse.From).ToList());
    }

    /// <summary>Get one document's current status/progress (polled while processing).</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<DocumentResponse>> Get(
        string pid,
        string id,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var document = await _services.Documents.GetAsync(pid, id, cancellationToken).ConfigureAwait(false);
        return document is null ? NotFound() : Ok(DocumentResponse.From(document));
    }

    /// <summary>
    /// Accept a multipart upload: store the bytes, insert a <c>processing</c> row,
    /// enqueue ingestion, and respond <c>202</c> with <c>{ id, status }</c> so the
    /// client can start polling.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Upload(
        string pid,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        // No file part → 400 ProblemDetails, never a 500 or a reach into the service.
        if (file is null || file.Length == 0)
        {
            return Problem(
                detail: "A non-empty 'file' part is required.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request");
        }

        var upload = new DocumentUpload
        {
            Filename = file.FileName,
            Mime = file.ContentType ?? "application/octet-stream",
            SizeBytes = file.Length,
            OpenReadStream = file.OpenReadStream,
        };

        var document = await _services.Documents.UploadAsync(pid, upload, cancellationToken).ConfigureAwait(false);

        // 202 + { id, status: "processing" } (rest-api.md). The status pill is driven
        // by polling Get; the Location points at the poll endpoint.
        return AcceptedAtAction(
            nameof(Get),
            new { pid, id = document.Id },
            new { id = document.Id, status = document.Status });
    }

    /// <summary>Delete a document, its chunks/vec/fts rows, and the original file.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(
        string pid,
        string id,
        CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var deleted = await _services.Documents.DeleteAsync(pid, id, cancellationToken).ConfigureAwait(false);
        return deleted ? NoContent() : NotFound();
    }
}
