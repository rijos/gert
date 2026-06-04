using System.IO.Compression;
using System.Text.Json;
using Gert.Service.Database;
using Gert.Service.Storage;

namespace Gert.Service.Account;

/// <summary>
/// Self-service data lifecycle for the whole account (rest-api.md § account):
/// export a single project or everything as a <c>.zip</c>, or erase all of the
/// caller's data. Identity comes only from <see cref="IUserContext"/>; identity
/// removal itself is the IdP's, not the API's.
///
/// <para>
/// Export streams a <c>.zip</c> built from each project's conversations (one
/// <c>conversations.json</c> per project, the full thread incl. messages) plus its
/// original file blobs — conversations via <see cref="IDatabaseProvider"/>, files
/// via <see cref="IObjectStore"/> (decision: user blobs only through the object
/// store). Delete-account is a directory <c>rm -rf</c> via <see cref="IUserStore"/>.
/// </para>
/// </summary>
public sealed class AccountService : IAccountService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IUserStore _store;
    private readonly IDatabaseProvider _databases;
    private readonly IObjectStore _objects;
    private readonly IUserContext _user;

    public AccountService(
        IUserStore store,
        IDatabaseProvider databases,
        IObjectStore objects,
        IUserContext user)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public async Task<ExportArchive> ExportAsync(CancellationToken cancellationToken = default)
    {
        await _databases.EnsureProvisionedAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);

        var projects = await _store.ListProjectsAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);
        var pids = projects.Select(p => p.Id).ToList();

        return await BuildArchiveAsync("gert-export.zip", pids, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ExportArchive> ExportProjectAsync(string pid, CancellationToken cancellationToken = default)
    {
        await _databases.EnsureProjectAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false);
        return await BuildArchiveAsync($"gert-project-{pid}.zip", [pid], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAccountAsync(CancellationToken cancellationToken = default)
    {
        // rm -rf users/{key} — erase all of the caller's data (not the IdP account).
        await _store.DeleteUserAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false);
    }

    // ---- archive build -----------------------------------------------------

    /// <summary>
    /// Build a <c>.zip</c> for the given pids into a throwaway temp file, then hand
    /// back a stream factory that opens it for the host and deletes it on close, so
    /// the service stays transport-agnostic and leaves nothing behind.
    /// </summary>
    private async Task<ExportArchive> BuildArchiveAsync(
        string fileName,
        IReadOnlyList<string> pids,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "gert-export-" + Guid.NewGuid().ToString("N") + ".zip");

        await using (var file = File.Create(tempPath))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var pid in pids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteProjectAsync(zip, pid, cancellationToken).ConfigureAwait(false);
            }
        }

        return new ExportArchive
        {
            FileName = fileName,
            ContentType = "application/zip",
            OpenReadAsync = _ => Task.FromResult<Stream>(
                new FileStream(
                    tempPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920,
                    FileOptions.DeleteOnClose | FileOptions.Asynchronous)),
        };
    }

    private async Task WriteProjectAsync(ZipArchive zip, string pid, CancellationToken cancellationToken)
    {
        // Conversations → projects/{pid}/conversations.json (full threads).
        var threads = new List<Gert.Model.Chat.ConversationThread>();
        await using (var chat = await _databases
            .OpenChatAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false))
        {
            var conversations = await chat.ListConversationsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var conversation in conversations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var thread = await chat.GetThreadAsync(conversation.Id, cancellationToken).ConfigureAwait(false);
                if (thread is not null)
                {
                    threads.Add(thread);
                }
            }
        }

        var conversationsEntry = zip.CreateEntry($"projects/{pid}/conversations.json", CompressionLevel.Optimal);
        await using (var entryStream = conversationsEntry.Open())
        {
            await JsonSerializer.SerializeAsync(entryStream, threads, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        // Original file blobs → projects/{pid}/files/{key} (via the object store only).
        var scope = new ObjectScope(_user.Iss, _user.Sub, pid);
        var keys = await _objects.ListAsync(scope, string.Empty, cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = zip.CreateEntry($"projects/{pid}/files/{key}", CompressionLevel.Optimal);
            await using var source = await _objects.OpenReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
            await using var target = entry.Open();
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }
}
