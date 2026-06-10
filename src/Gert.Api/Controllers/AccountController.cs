using Gert.Api.Validation;
using Gert.Service.Account;
using Gert.Service.Documents;
using Microsoft.AspNetCore.Mvc;

namespace Gert.Api.Controllers;

/// <summary>
/// Self-service data lifecycle (rest-api.md § account &amp; data): per-project
/// forget/export, whole-account export, and account erase. Identity is implicit
/// from the token; the project routes validate <c>{pid}</c> first. Identity removal
/// itself is the IdP's, not the API's. Covered by the fallback authenticated-user
/// policy.
/// </summary>
[ApiController]
[Route("api")]
public sealed class AccountController : ControllerBase
{
    // Granular interfaces, not the IGertServices hub (dotnet-style-guide.md §4).
    private readonly IAccountService _account;
    private readonly IDocumentService _documents;

    public AccountController(IAccountService account, IDocumentService documents)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
    }

    /// <summary>Wipe a project's RAG corpus + files, keeping its chats.</summary>
    [HttpPost("projects/{pid}/forget-documents")]
    public async Task<IActionResult> ForgetDocuments(string pid, CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        await _documents.ForgetAllAsync(pid, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Download a single project (conversations + original files).</summary>
    [HttpGet("projects/{pid}/export")]
    public async Task<IActionResult> ExportProject(string pid, CancellationToken cancellationToken)
    {
        RouteParams.RequireValidProjectId(pid);

        var archive = await _account.ExportProjectAsync(pid, cancellationToken).ConfigureAwait(false);
        return await StreamArchiveAsync(archive, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Download everything — all projects.</summary>
    [HttpGet("account/export")]
    public async Task<IActionResult> ExportAll(CancellationToken cancellationToken)
    {
        var archive = await _account.ExportAsync(cancellationToken).ConfigureAwait(false);
        return await StreamArchiveAsync(archive, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>rm -rf users/{key}</c> — erase all of the caller's data (not the IdP account).</summary>
    [HttpDelete("account")]
    public async Task<IActionResult> DeleteAccount(CancellationToken cancellationToken)
    {
        await _account.DeleteAccountAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private async Task<IActionResult> StreamArchiveAsync(
        ExportArchive archive,
        CancellationToken cancellationToken)
    {
        var stream = await archive.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        return File(stream, archive.ContentType, archive.FileName);
    }
}
