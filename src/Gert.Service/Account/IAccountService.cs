namespace Gert.Service.Account;

/// <summary>
/// Self-service data lifecycle for the whole account (rest-api.md section account):
/// export everything, or erase all of this user's data. Identity removal is the
/// IdP's, not the API's.
/// </summary>
public interface IAccountService
{
    /// <summary>Export everything - all projects - as a downloadable archive.</summary>
    Task<ExportArchive> ExportAsync(CancellationToken cancellationToken = default);

    /// <summary>Export a single project (conversations + original files).</summary>
    Task<ExportArchive> ExportProjectAsync(string pid, CancellationToken cancellationToken = default);

    /// <summary>Erase all of this user's data across its stores (databases + blobs).</summary>
    Task DeleteAccountAsync(CancellationToken cancellationToken = default);
}
