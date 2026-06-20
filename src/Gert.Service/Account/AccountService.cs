using System.IO.Compression;
using System.Text.Json;
using Gert.Database;
using Gert.Storage;

namespace Gert.Service.Account;

/// <summary>
/// Self-service account data lifecycle (rest-api.md section account): export one
/// project or everything as a <c>.zip</c>, or erase all of the caller's data.
/// Identity comes only from <see cref="IUserContext"/>; removing the identity itself
/// is the IdP's job, not the API's.
///
/// <para>
/// Export builds the <c>.zip</c> from the project list in <c>user.db</c>
/// (<see cref="IUserDatabaseProvider"/>), conversations via
/// <see cref="IChatDatabaseProvider"/>, and file blobs via <see cref="IObjectStore"/>
/// (decision: user blobs only through the object store). Delete-account delegates to
/// <see cref="IUserDataEraser"/>, which erases all three independent stores (databases +
/// RAG index + blobs) crash-consistently under the deletion journal.
/// </para>
/// </summary>
public sealed class AccountService : IAccountService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IUserDatabaseProvider _userDatabases;
    private readonly IChatDatabaseProvider _chatDatabases;
    private readonly IObjectStore _objects;
    private readonly IUserContext _user;
    private readonly IUserDataEraser _eraser;

    public AccountService(
        IUserDatabaseProvider userDatabases,
        IChatDatabaseProvider chatDatabases,
        IObjectStore objects,
        IUserContext user,
        IUserDataEraser eraser)
    {
        _userDatabases = userDatabases ?? throw new ArgumentNullException(nameof(userDatabases));
        _chatDatabases = chatDatabases ?? throw new ArgumentNullException(nameof(chatDatabases));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _eraser = eraser ?? throw new ArgumentNullException(nameof(eraser));
    }

    /// <inheritdoc />
    public async Task<ExportArchive> ExportAsync(CancellationToken cancellationToken = default)
    {
        List<string> pids;
        await using (var repo = await _userDatabases.OpenAsync(_user.Iss, _user.Sub, cancellationToken).ConfigureAwait(false))
        {
            var projects = await repo.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
            pids = projects.Select(p => p.Id).ToList();
        }

        return await BuildArchiveAsync("gert-export.zip", pids, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ExportArchive> ExportProjectAsync(string pid, CancellationToken cancellationToken = default)
    {
        return await BuildArchiveAsync($"gert-project-{pid}.zip", [pid], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteAccountAsync(CancellationToken cancellationToken = default) =>
        // Erase all of the caller's data (not the IdP account) through the journal-guarded
        // eraser, so a crash mid-delete is resumable rather than a half-erased account. All
        // stores self-provision again on next open.
        _eraser.EraseAsync(StorageKeys.UserKey(_user.Iss, _user.Sub), cancellationToken);

    /// <summary>
    /// Build the <c>.zip</c> into a throwaway temp file, then hand back a stream
    /// factory over it, so the service stays transport-agnostic and leaves nothing
    /// behind. Cleanup is robust by construction: a failed/cancelled build deletes the
    /// temp file in the catch, and the returned read stream carries
    /// <see cref="FileOptions.DeleteOnClose"/> so the OS removes the file once the host
    /// closes it (or the handle's finalizer fires if the factory is never invoked).
    /// </summary>
    private async Task<ExportArchive> BuildArchiveAsync(
        string fileName,
        IReadOnlyList<string> pids,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "gert-export-" + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            await using (var file = File.Create(tempPath))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var pid in pids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WriteProjectAsync(zip, pid, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // A failed/cancelled build must not strand the partial archive
            // (dotnet-style-guide.md section 7: best-effort cleanup, original fault wins).
            TryDelete(tempPath);
            throw;
        }

        // Open the read stream NOW and transfer ownership to the archive: with
        // DeleteOnClose the file's lifetime is tied to this handle, so it cannot
        // outlive the response (close -> delete) nor an abandoned archive (the
        // SafeFileHandle finalizer closes it eventually).
        var stream = new FileStream(
            tempPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        return new ExportArchive
        {
            FileName = fileName,
            ContentType = "application/zip",
            // Single-use by contract: the host opens it once and streams it out.
            OpenReadAsync = _ => Task.FromResult<Stream>(stream),
        };
    }

    /// <summary>Best-effort temp-file removal; the original exception stays the story.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Swallowed by design: cleanup of a throwaway temp file must never mask
            // the build failure; an undeletable file is left to the OS temp sweeper.
        }
        catch (UnauthorizedAccessException)
        {
            // Same degrade decision as above.
        }
    }

    private async Task WriteProjectAsync(ZipArchive zip, string pid, CancellationToken cancellationToken)
    {
        // Conversations -> projects/{pid}/conversations.json (full threads).
        var threads = new List<Gert.Model.Chat.ConversationThread>();
        await using (var chat = await _chatDatabases
            .OpenAsync(_user.Iss, _user.Sub, pid, cancellationToken).ConfigureAwait(false))
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

        // Original file blobs -> projects/{pid}/{key} (via the object store only).
        // Listed by prefix so the database files are never pulled into the archive.
        var scope = ObjectScope.Project(_user.Iss, _user.Sub, pid);
        foreach (var prefix in new[] { "files/" })
        {
            var keys = await _objects.ListAsync(scope, prefix, cancellationToken).ConfigureAwait(false);
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = zip.CreateEntry($"projects/{pid}/{key}", CompressionLevel.Optimal);
                await using var source = await _objects.OpenReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
                await using var target = entry.Open();
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
