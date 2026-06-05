using Gert.Model;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Service.Database;
using Gert.Service.Storage;
using Gert.Service.Validation;

namespace Gert.Service.Projects;

/// <summary>
/// Manages the caller's projects (rest-api.md § projects; configuration.md § 2) —
/// orchestrates the config seam (<see cref="IUserStore"/> for
/// <c>projects/{pid}/meta.json</c>) and the provisioning/persistence seam
/// (<see cref="IDatabaseProvider"/> for the per-project <c>chat.db</c>/<c>rag.db</c>
/// + count rollups). Identity comes only from <see cref="IUserContext"/>; the caller
/// supplies only the <c>pid</c>, so a request can never reach another user's data.
///
/// <para>
/// Create mints a fresh UUID pid, calls <see cref="IDatabaseProvider.EnsureProjectAsync"/>
/// (which materialises the folder + databases), then overwrites <c>meta.json</c> with
/// the requested fields. Delete <c>rm -rf</c>s the project; the <c>default</c> project
/// is <b>emptied and re-provisioned</b>, never removed (configuration.md § 5).
/// </para>
/// </summary>
public sealed class ProjectService : IProjectService
{
    private readonly IUserStore _store;
    private readonly IDatabaseProvider _databases;
    private readonly IValidationProvider _validation;
    private readonly IUserContext _user;

    public ProjectService(
        IUserStore store,
        IDatabaseProvider databases,
        IValidationProvider validation,
        IUserContext user)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        // Ensure the user folder + default project exist so a brand-new account lists
        // at least its default project rather than an empty set.
        await _databases.EnsureProvisionedAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        var metas = await _store.ListProjectsAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

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
        await _databases.EnsureProvisionedAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        var meta = await _store.GetProjectAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false);
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

        var pid = Guid.NewGuid().ToString("D");

        // Materialise the folder + chat.db/rag.db (writes a stub meta.json), then
        // overwrite meta.json with the requested config (REUSE the provisioning seam).
        await _databases.EnsureProjectAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var meta = new ProjectMeta
        {
            Id = pid,
            Name = request.Name,
            Description = request.Description,
            Instructions = request.Instructions,
            Defaults = request.Defaults,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _store.SaveProjectAsync(_user.Iss, _user.Sub, meta, cancellationToken).ConfigureAwait(false);
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

        await _databases.EnsureProvisionedAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        var current = await _store.GetProjectAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false);
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
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await _store.SaveProjectAsync(_user.Iss, _user.Sub, merged, cancellationToken).ConfigureAwait(false);
        return merged;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string pid, CancellationToken cancellationToken = default)
    {
        await _databases.EnsureProvisionedAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        var existing = await _store.GetProjectAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        // The default project is emptied + re-provisioned, never removed (config § 5).
        if (string.Equals(pid, DefaultProjectId, StringComparison.Ordinal))
        {
            await _store.EmptyProjectAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false);
            await _databases.EnsureProjectAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return await _store.DeleteProjectAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>The literal landing-project id (storage-and-data.md § layout).</summary>
    private const string DefaultProjectId = "default";

    private async Task<ProjectSummary> SummariseAsync(ProjectMeta meta, CancellationToken cancellationToken)
    {
        int conversationCount;
        await using (var chat = await _databases
            .OpenChatAsync(_user.Iss, _user.Sub, meta.Id, cancellationToken).ConfigureAwait(false))
        {
            var conversations = await chat.ListConversationsAsync(cancellationToken).ConfigureAwait(false);
            conversationCount = conversations.Count;
        }

        int documentCount;
        int memoryCount;
        await using (var rag = await _databases
            .OpenRagAsync(_user.Iss, _user.Sub, meta.Id, cancellationToken).ConfigureAwait(false))
        {
            var documents = await rag.ListDocumentsAsync(DocumentKind.Document, cancellationToken).ConfigureAwait(false);
            var memories = await rag.ListDocumentsAsync(DocumentKind.Memory, cancellationToken).ConfigureAwait(false);
            documentCount = documents.Count;
            memoryCount = memories.Count;
        }

        return ProjectSummary.From(meta, conversationCount, documentCount, memoryCount);
    }
}
