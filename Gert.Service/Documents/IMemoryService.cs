using Gert.Model.Dtos;
using Gert.Model.Rag;

namespace Gert.Service.Documents;

/// <summary>
/// Manages a project's memory entries — markdown notes embedded into the
/// project's <c>rag.db</c> as <c>kind='memory'</c> (rest-api.md § memory;
/// configuration.md § 2.3).
/// </summary>
public interface IMemoryService
{
    /// <summary>List entries (id, title, pinned, updated_at).</summary>
    Task<IReadOnlyList<MemoryEntry>> ListAsync(
        string pid,
        CancellationToken cancellationToken = default);

    /// <summary>Add or edit an entry; it is (re)embedded for retrieval.</summary>
    Task<MemoryEntry> UpsertAsync(
        string pid,
        CreateMemoryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Remove an entry and its chunks.</summary>
    Task<bool> DeleteAsync(
        string pid,
        string memoryId,
        CancellationToken cancellationToken = default);
}
