namespace Gert.Service.Account;

/// <summary>
/// Stub. Account-wide export/erase walk the user's data root (all projects'
/// folders + databases). The data-root seam isn't wired into the service layer
/// yet, so this is deferred.
/// <para>
/// // TODO U7c: implement export (zip the user folder) + delete
/// (<c>rm -rf users/{key}</c>) against the host-provided data root.
/// </para>
/// Present so <see cref="GertServices"/> + DI compile for the M1 gate.
/// </summary>
public sealed class AccountService : IAccountService
{
    /// <inheritdoc />
    public Task<ExportArchive> ExportAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");

    /// <inheritdoc />
    public Task<ExportArchive> ExportProjectAsync(string pid, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");

    /// <inheritdoc />
    public Task DeleteAccountAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");
}
