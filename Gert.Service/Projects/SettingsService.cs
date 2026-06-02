using Gert.Model.Dtos;
using Gert.Model.Projects;

namespace Gert.Service.Projects;

/// <summary>
/// Stub. The user's preferences live in <c>settings.json</c> at the user root;
/// get/update read/write that file. The data-root seam isn't wired into the
/// service layer yet, so this is deferred.
/// <para>
/// // TODO U7c: implement against the host-provided data root
/// (read/merge/write <c>settings.json</c>).
/// </para>
/// Present so <see cref="GertServices"/> + DI compile for the M1 gate.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    /// <inheritdoc />
    public Task<UserSettings> GetAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");

    /// <inheritdoc />
    public Task<UserSettings> UpdateAsync(
        UpdateSettingsRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U7c");
}
