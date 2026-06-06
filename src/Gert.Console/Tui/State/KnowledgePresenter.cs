using System.Text;
using Gert.Model.Dtos;
using Gert.Model.Rag;
using Gert.Service;
using Gert.Service.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Console.Tui.State;

/// <summary>
/// The knowledge panel's model (U16) — documents + memory for the active
/// project, the console analog of <c>knowledge-panel.js</c>. Upload reads a
/// local file path (the console's "drop zone" is the filesystem); ingestion
/// runs inline, so returned documents carry terminal status.
/// </summary>
public sealed class KnowledgePresenter
{
    private readonly IServiceProvider _services;

    public KnowledgePresenter(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>The active project (the shell keeps it in sync with the sidebar).</summary>
    public string Pid { get; set; } = "default";

    /// <summary>List the project's uploaded documents (excludes memory rows).</summary>
    public async Task<IReadOnlyList<Document>> ListDocumentsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
        return await gert.Documents.ListAsync(Pid, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>List the project's memory entries.</summary>
    public async Task<IReadOnlyList<MemoryEntry>> ListMemoryAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
        return await gert.Memory.ListAsync(Pid, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Upload a local file; ingestion is inline, so the re-fetched document
    /// carries its terminal status (<c>Ready</c>/<c>Failed</c>).
    /// </summary>
    public async Task<Document> UploadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"file not found: {path}", path);
        }

        var info = new FileInfo(path);
        var upload = new DocumentUpload
        {
            Filename = Path.GetFileName(path),
            Mime = MimeForExtension(Path.GetExtension(path)),
            SizeBytes = info.Length,
            OpenReadStream = () => File.OpenRead(path),
        };

        await using var scope = _services.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
        var created = await gert.Documents.UploadAsync(Pid, upload, cancellationToken).ConfigureAwait(false);
        return await gert.Documents.GetAsync(Pid, created.Id, cancellationToken).ConfigureAwait(false) ?? created;
    }

    /// <summary>Delete a document and its chunks.</summary>
    public async Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        await using var scope = _services.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
        await gert.Documents.DeleteAsync(Pid, documentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Add or edit a memory entry ((re)embedded on write).</summary>
    public async Task<MemoryEntry> UpsertMemoryAsync(
        string title,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        await using var scope = _services.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
        return await gert.Memory
            .UpsertAsync(Pid, new CreateMemoryRequest { Title = title, Content = content }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Remove a memory entry.</summary>
    public async Task DeleteMemoryAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(memoryId);
        await using var scope = _services.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
        await gert.Memory.DeleteAsync(Pid, memoryId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decode the base64 display filename (display metadata, never a path).</summary>
    public static string DisplayName(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(document.Filename));
        }
        catch (FormatException)
        {
            return document.Filename;
        }
    }

    /// <summary>Map a file extension to its allowed MIME type (UploadConstraints).</summary>
    public static string MimeForExtension(string extension) =>
        (extension ?? string.Empty).TrimStart('.').ToLowerInvariant() switch
        {
            "pdf" => "application/pdf",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "md" => "text/markdown",
            "txt" => "text/plain",
            _ => "application/octet-stream",
        };
}
