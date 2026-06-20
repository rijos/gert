using Gert.Database;
using Gert.Model;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Rag;
using Gert.Storage;
using Gert.Validation;

namespace Gert.Service.Projects;

/// <summary>
/// Manages the caller's projects (rest-api.md section projects; configuration.md section 2).
/// The registry (id, name, description, instructions, defaults) lives in <c>user.db</c>;
/// per-project conversation/document counts come from each project's
/// <c>chat.db</c>/<c>rag.db</c>, which self-provision on open. Identity comes only from
/// <see cref="IUserContext"/>. Create mints a fresh UUID pid; databases materialise
/// lazily on first open. Delete drops the project's databases + blobs and removes the
/// registry row, except the <c>default</c> project, which is emptied and kept
/// (configuration.md section 5).
/// </summary>
public sealed class ProjectService : IProjectService
{
    /// <summary>The literal landing-project id (storage-and-data.md section layout).</summary>
    private const string DefaultProjectId = StorageKeys.DefaultProjectId;

    private readonly IUserDatabaseProvider _userDatabases;
    private readonly IChatDatabaseProvider _chatDatabases;
    private readonly IRagIndexProvider _ragDatabases;
    private readonly IObjectStore _objects;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;

    public ProjectService(
        IUserDatabaseProvider userDatabases,
        IChatDatabaseProvider chatDatabases,
        IRagIndexProvider ragDatabases,
        IObjectStore objects,
        IUserContext user,
        TimeProvider time)
    {
        _userDatabases = userDatabases ?? throw new ArgumentNullException(nameof(userDatabases));
        _chatDatabases = chatDatabases ?? throw new ArgumentNullException(nameof(chatDatabases));
        _ragDatabases = ragDatabases ?? throw new ArgumentNullException(nameof(ragDatabases));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
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
        Validated<CreateProjectRequest> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dto = request.Value;

        // Injected clock (dotnet-style-guide.md section 5) so tests can pin the timestamps.
        var now = _time.GetUtcNow();
        var meta = new ProjectMeta
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = dto.Name,
            Description = dto.Description,
            Instructions = dto.Instructions,
            Defaults = dto.Defaults,
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
        Validated<UpdateProjectRequest> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dto = request.Value;

        await using var repo = await _userDatabases.OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        var current = await repo.GetProjectAsync(pid, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        // Partial merge: each field overrides only when present (null = unchanged).
        var merged = current with
        {
            Name = dto.Name ?? current.Name,
            Description = dto.Description ?? current.Description,
            Instructions = dto.Instructions ?? current.Instructions,
            Defaults = dto.Defaults ?? current.Defaults,
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
        await using (var rag = await _ragDatabases
            .OpenAsync(_user.Iss, _user.Sub, meta.Id, cancellationToken).ConfigureAwait(false))
        {
            var documents = await rag.ListDocumentsAsync(cancellationToken).ConfigureAwait(false);
            documentCount = documents.Count;
        }

        return ProjectSummary.From(meta, conversationCount, documentCount);
    }
}
