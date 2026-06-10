using Gert.Model;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Database;
using Gert.Service.Storage;
using Gert.Service.Validation;

namespace Gert.Service.Projects;

/// <summary>
/// Manages the caller's projects (rest-api.md § projects; configuration.md § 2).
/// The project registry (id, name, description, instructions, defaults) lives in
/// <c>user.db</c> via <see cref="IUserDatabaseProvider"/>; per-project conversation
/// and document counts come from each project's <c>chat.db</c>/<c>rag.db</c>
/// (<see cref="IChatDatabaseProvider"/>/<see cref="IRagDatabaseProvider"/>), which
/// self-provision on open. Blob/database directory lifecycle (delete/empty) is the
/// <see cref="IUserStore"/>'s. Identity comes only from <see cref="IUserContext"/>.
///
/// <para>
/// Create mints a fresh UUID pid and registers it; the project's databases
/// materialise lazily on first open. Delete removes the registry row and
/// <c>rm -rf</c>s the project scope; the <c>default</c> project is emptied and kept
/// (configuration.md § 5).
/// </para>
/// </summary>
public sealed class ProjectService : IProjectService
{
    /// <summary>The literal landing-project id (storage-and-data.md § layout).</summary>
    private const string DefaultProjectId = StorageKeys.DefaultProjectId;

    private readonly IUserDatabaseProvider _userDatabases;
    private readonly IChatDatabaseProvider _chatDatabases;
    private readonly IRagDatabaseProvider _ragDatabases;
    private readonly IUserStore _store;
    private readonly IValidationProvider _validation;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;

    public ProjectService(
        IUserDatabaseProvider userDatabases,
        IChatDatabaseProvider chatDatabases,
        IRagDatabaseProvider ragDatabases,
        IUserStore store,
        IValidationProvider validation,
        IUserContext user,
        TimeProvider time)
    {
        _userDatabases = userDatabases ?? throw new ArgumentNullException(nameof(userDatabases));
        _chatDatabases = chatDatabases ?? throw new ArgumentNullException(nameof(chatDatabases));
        _ragDatabases = ragDatabases ?? throw new ArgumentNullException(nameof(ragDatabases));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProjectMeta> metas;
        await using (var repo = await _userDatabases.OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false))
        {
            metas = await repo.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
        }

        var summaries = new List<ProjectSummary>(metas.Count);
        foreach (var meta in metas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            summaries.Add(await SummariseAsync(meta, cancellationToken).ConfigureAwait(false));
        }

        return summaries;
    }

    /// <inheritdoc />
    public async Task<ProjectSummary?> GetAsync(string pid, CancellationToken cancellationToken = default)
    {
        ProjectMeta? meta;
        await using (var repo = await _userDatabases.OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false))
        {
            meta = await repo.GetProjectAsync(pid, cancellationToken).ConfigureAwait(false);
        }

        return meta is null ? null : await SummariseAsync(meta, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProjectMeta> CreateAsync(
        CreateProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = _validation.Validate(request);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation);
        }

        // Injected clock (dotnet-style-guide.md §5) so tests can pin the timestamps.
        var now = _time.GetUtcNow();
        var meta = new ProjectMeta
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = request.Name,
            Description = request.Description,
            Instructions = request.Instructions,
            Defaults = request.Defaults,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Register the project; its chat.db/rag.db materialise lazily on first open.
        await using var repo = await _userDatabases.OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);
        await repo.SaveProjectAsync(meta, cancellationToken).ConfigureAwait(false);
        return meta;
    }

    /// <inheritdoc />
    public async Task<ProjectMeta?> UpdateAsync(
        string pid,
        UpdateProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = _validation.Validate(request);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation);
        }

        await using var repo = await _userDatabases.OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        var current = await repo.GetProjectAsync(pid, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        // Partial merge: each field overrides only when present (null = unchanged).
        var merged = current with
        {
            Name = request.Name ?? current.Name,
            Description = request.Description ?? current.Description,
            Instructions = request.Instructions ?? current.Instructions,
            Defaults = request.Defaults ?? current.Defaults,
            UpdatedAt = _time.GetUtcNow(),
        };

        await repo.SaveProjectAsync(merged, cancellationToken).ConfigureAwait(false);
        return merged;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string pid, CancellationToken cancellationToken = default)
    {
        await using var repo = await _userDatabases.OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        var existing = await repo.GetProjectAsync(pid, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        // The default project is emptied + kept, never removed (config § 5); its
        // databases re-materialise on the next open.
        if (string.Equals(pid, DefaultProjectId, StringComparison.Ordinal))
        {
            await _store.EmptyProjectAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // rm -rf the project scope (blobs + chat.db/rag.db), then drop the registry row.
        await _store.DeleteProjectAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false);
        return await repo.DeleteProjectAsync(pid, cancellationToken).ConfigureAwait(false);
    }

    // ---- helpers -----------------------------------------------------------

    private async Task<ProjectSummary> SummariseAsync(ProjectMeta meta, CancellationToken cancellationToken)
    {
        int conversationCount;
        await using (var chat = await _chatDatabases
            .OpenAsync(_user.Iss, _user.Sub, meta.Id, cancellationToken).ConfigureAwait(false))
        {
            var conversations = await chat.ListConversationsAsync(cancellationToken).ConfigureAwait(false);
            conversationCount = conversations.Count;
        }

        int documentCount;
        int memoryCount;
        await using (var rag = await _ragDatabases
            .OpenAsync(_user.Iss, _user.Sub, meta.Id, cancellationToken).ConfigureAwait(false))
        {
            var documents = await rag.ListDocumentsAsync(DocumentKind.Document, cancellationToken).ConfigureAwait(false);
            var memories = await rag.ListDocumentsAsync(DocumentKind.Memory, cancellationToken).ConfigureAwait(false);
            documentCount = documents.Count;
            memoryCount = memories.Count;
        }

        return ProjectSummary.From(meta, conversationCount, documentCount, memoryCount);
    }
}
