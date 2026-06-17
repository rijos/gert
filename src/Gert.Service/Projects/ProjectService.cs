using Gert.Database;
using Gert.Model;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Model.Rag;
using Gert.Rag;
using Gert.Service.Validation;
using Gert.Storage;

namespace Gert.Service.Projects;

/// <summary>
/// Manages the caller's projects (rest-api.md section projects; configuration.md section 2).
/// The project registry (id, name, description, instructions, defaults) lives in
/// <c>user.db</c> via <see cref="IUserDatabaseProvider"/>; per-project conversation
/// and document counts come from each project's <c>chat.db</c>/<c>rag.db</c>
/// (<see cref="IChatDatabaseProvider"/>/<see cref="IRagIndexProvider"/>), which
/// self-provision on open. Delete/empty orchestrates the database half (the chat/rag
/// providers' <c>DeleteProjectAsync</c>) and the artifact half
/// (<see cref="IObjectStore"/>). Identity comes only from <see cref="IUserContext"/>.
///
/// <para>
/// Create mints a fresh UUID pid and registers it; the project's databases
/// materialise lazily on first open. Delete drops the project's databases + blobs and
/// removes the registry row; the <c>default</c> project is emptied and kept
/// (configuration.md section 5).
/// </para>
/// </summary>
public sealed class ProjectService : IProjectService
{
    /// <summary>The literal landing-project id (storage-and-data.md section layout).</summary>
    private const string DefaultProjectId = StorageKeys.DefaultProjectId;

    private readonly IUserDatabaseProvider _userDatabases;
    private readonly IChatDatabaseProvider _chatDatabases;
    private readonly IRagIndexProvider _ragDatabases;
    private readonly IObjectStore _objects;
    private readonly IValidationProvider _validation;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;

    public ProjectService(
        IUserDatabaseProvider userDatabases,
        IChatDatabaseProvider chatDatabases,
        IRagIndexProvider ragDatabases,
        IObjectStore objects,
        IValidationProvider validation,
        IUserContext user,
        TimeProvider time)
    {
        _userDatabases = userDatabases ?? throw new ArgumentNullException(nameof(userDatabases));
        _chatDatabases = chatDatabases ?? throw new ArgumentNullException(nameof(chatDatabases));
        _ragDatabases = ragDatabases ?? throw new ArgumentNullException(nameof(ragDatabases));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Page-size ceiling for the search overlay (defensive clamp).</summary>
    private const int MaxPageSize = 100;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(
        string? query = null,
        int limit = 0,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProjectMeta> metas;
        await using (var repo = await _userDatabases.OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false))
        {
            metas = await repo.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
        }

        // Filter + page BEFORE summarising - each summary opens the project's
        // databases for counts, so the page bound also bounds the disk work.
        IEnumerable<ProjectMeta> page = metas;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            page = page.Where(m => m.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        if (offset > 0)
        {
            page = page.Skip(offset);
        }

        if (limit > 0)
        {
            page = page.Take(Math.Min(limit, MaxPageSize));
        }

        var selected = page.ToList();
        var summaries = new List<ProjectSummary>(selected.Count);
        foreach (var meta in selected)
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

        // Injected clock (dotnet-style-guide.md section 5) so tests can pin the timestamps.
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

        var scope = ObjectScope.Project(_user.Iss, _user.Sub, pid);

        // Drop the database half first (principle #5): the chat/rag providers release the
        // engine's pooled handles + remove chat.db/rag.db, so the blob delete that follows
        // never races an open file. Both re-materialise lazily on the next open.
        await _chatDatabases.DeleteProjectAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false);
        await _ragDatabases.DeleteProjectAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false);

        // The default project is emptied + kept, never removed (config section 5): clear
        // its blobs but keep the scope root and the registry row.
        if (string.Equals(pid, DefaultProjectId, StringComparison.Ordinal))
        {
            await _objects.DeletePrefixAsync(scope, string.Empty, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // A non-default project: remove the blob scope, then drop the registry row.
        await _objects.DeleteScopeAsync(scope, cancellationToken).ConfigureAwait(false);
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
