using Gert.Rag;
using Gert.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gert.Service.Ingestion;

/// <summary>
/// The document ingestion pipeline (chat-and-tools.md section ingestion):
/// open -> extract -> chunk -> embed -> write, host-agnostic and run by either the
/// inline queue or the background worker. It <b>never throws out of the worker
/// path</b>: any failure (no text, an unavailable extractor, an embed/write error)
/// is recorded as <c>status='failed'</c> on the document so one bad upload can
/// never crash the worker (the ingestion analog of the run_python sandbox).
///
/// <para>
/// All file bytes are read through <see cref="IObjectStore"/> - never a raw path
/// (decision: files via IObjectStore). The blob lives under the project's
/// <c>files/</c> at <see cref="IngestJob.ObjectKey"/> (<c>files/{doc-id}</c>, no extension).
/// </para>
/// </summary>
public sealed class IngestionService : IIngestionService
{
    private readonly IRagIndexProvider _databases;
    private readonly IObjectStore _objects;
    private readonly ITextExtractor _extractor;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly ChunkingOptions _chunking;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        IRagIndexProvider databases,
        IObjectStore objects,
        ITextExtractor extractor,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        ChunkingOptions? chunking = null,
        ILogger<IngestionService>? logger = null)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _chunking = chunking ?? ChunkingOptions.Default;
        _logger = logger ?? NullLogger<IngestionService>.Instance;
    }

    /// <inheritdoc />
    public async Task IngestAsync(
        IngestJob job,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var repo = await _databases
            .OpenAsync(job.Iss, job.Sub, job.Pid, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await RunAsync(job, repo, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Honour cancellation - don't bury it as a document failure.
            throw;
        }
        catch (Exception ex)
        {
            // Fail the document, never the worker (chat-and-tools.md section hardened extraction).
            await TryFailAsync(repo, job.DocumentId, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(
        IngestJob job,
        IRagStore repo,
        IProgress<IngestionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var document = await repo.GetDocumentAsync(job.DocumentId, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            // Row vanished (deleted mid-flight) - nothing to do.
            return;
        }

        var scope = ObjectScope.Project(job.Iss, job.Sub, job.Pid);

        // Read bytes ONLY via the object store, never a raw path.
        ExtractionResult extraction;
        await using (var content = await _objects.OpenReadAsync(scope, job.ObjectKey, cancellationToken).ConfigureAwait(false))
        {
            extraction = await _extractor
                .ExtractAsync(content, job.Extension, cancellationToken)
                .ConfigureAwait(false);
        }

        // No usable text -> failed (decisions section 5). Distinguish an unavailable
        // extractor (carries its own Error) from a text-less file.
        if (!extraction.HasText)
        {
            var error = extraction.Error ?? "no extractable text";
            await FailAsync(repo, document, error, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Token windows with overlap, locator carried through.
        var chunks = TextChunker.Chunk(extraction.Pages, _chunking);
        if (chunks.Count == 0)
        {
            await FailAsync(repo, document, "no extractable text", cancellationToken).ConfigureAwait(false);
            return;
        }

        var total = chunks.Count;
        progress?.Report(new IngestionProgress { ChunksEmbedded = 0, ChunksTotal = total });

        var batchSize = Math.Max(1, _chunking.EmbedBatchSize);
        var embedded = 0;
        for (var offset = 0; offset < total; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = chunks.Skip(offset).Take(batchSize).ToList();
            var vectors = await _embeddings
                .GenerateAsync(batch.Select(c => c.Content).ToList(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (vectors.Count != batch.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding generator returned {vectors.Count} vectors for {batch.Count} chunks.");
            }

            var inserts = new List<ChunkInsert>(batch.Count);
            for (var i = 0; i < batch.Count; i++)
            {
                var chunk = batch[i];
                inserts.Add(new ChunkInsert
                {
                    DocumentId = document.Id,
                    Ordinal = chunk.Ordinal,
                    Content = chunk.Content,
                    Page = chunk.Locator,
                    TokenCount = chunk.TokenCount,
                    Embedding = vectors[i].Vector.ToArray(),
                });
            }

            await repo.InsertChunksAsync(inserts, cancellationToken).ConfigureAwait(false);

            embedded += batch.Count;
            progress?.Report(new IngestionProgress { ChunksEmbedded = embedded, ChunksTotal = total });
        }

        await repo.UpdateDocumentAsync(
            document with { Status = Model.Rag.DocumentStatus.Ready, ChunkCount = total, Error = null },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task FailAsync(
        IRagStore repo,
        Model.Rag.Document document,
        string error,
        CancellationToken cancellationToken)
    {
        // Compensate first: chunk batches commit per batch, so a mid-pipeline
        // failure can leave already-inserted chunks behind. Delete them so a
        // failed document leaves nothing retrievable - the row deletion here and
        // HybridSearchAsync's status='ready' join predicate are the two ends of
        // the same guarantee. A no-op when nothing was inserted yet.
        await repo.DeleteChunksAsync(document.Id, cancellationToken).ConfigureAwait(false);

        await repo.UpdateDocumentAsync(
            document with { Status = Model.Rag.DocumentStatus.Failed, Error = error, ChunkCount = 0 },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort failure marker for the catch-all path: re-load the row (it may
    /// have advanced) and mark it failed, swallowing any secondary error so the
    /// worker never throws. The swallow is logged (dotnet-style-guide.md section 7: every
    /// intentional catch-and-continue gets a comment AND a warning).
    /// </summary>
    private async Task TryFailAsync(
        IRagStore repo,
        string documentId,
        string error,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await repo.GetDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
            if (document is not null)
            {
                await FailAsync(repo, document, error, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Degrade decision: the document could not even be marked failed (db
            // gone, cancelled). Nothing more to do - the worker must not throw on
            // the failure path; the orphaned 'processing' row is the visible trace.
            // Logged so the double fault is diagnosable (no content, only the id).
            _logger.LogWarning(
                ex,
                "Failed to mark document {DocumentId} as failed after an ingestion error; leaving the row as-is",
                documentId);
        }
    }
}
