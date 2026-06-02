using Gert.Model.Dtos;
using Gert.Model.Rag;

namespace Gert.Service.Documents;

/// <summary>
/// Stub. Memory entries are markdown notes embedded into the project's
/// <c>rag.db</c> as <c>kind='memory'</c>; the full lifecycle (upsert + re-embed,
/// list, delete) lands in U4b (rag.db repo) + U7d. Present so
/// <see cref="GertServices"/> + DI compile for the M1 gate.
/// </summary>
public sealed class MemoryService : IMemoryService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryEntry>> ListAsync(
        string pid,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U4b/U7d");

    /// <inheritdoc />
    public Task<MemoryEntry> UpsertAsync(
        string pid,
        CreateMemoryRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U4b/U7d");

    /// <inheritdoc />
    public Task<bool> DeleteAsync(
        string pid,
        string memoryId,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO U4b/U7d");
}
