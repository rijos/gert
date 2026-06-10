using Gert.Database;
using Gert.Service.External;
using Gert.Service.Storage;

namespace Gert.Service.Ingestion;

/// <summary>
/// The document ingestion pipeline (chat-and-tools.md § ingestion):
/// open → extract → chunk → embed → write, host-agnostic and run by either the
/// inline queue or the U9b background worker. It <b>never throws out of the worker
/// path</b>: any failure (no text, an unavailable extractor, an embed/write error)
/// is recorded as <c>status='failed'</c> on the document so one bad upload can
/// never crash the worker (the ingestion analog of the run_python sandbox).
///
/// <para>
/// All file bytes are read through <see cref="IObjectStore"/> — never a raw path
/// (decision: files via IObjectStore). The blob lives under the project's
/// <c>files/</c> at <see cref="IngestJob.ObjectKey"/> (<c>{doc-id}.{ext}</c>).
/// </para>
/// </summary>
public sealed class IngestionService : IIngestionService
{
    private readonly IRagDatabaseProvider _databases;
    private readonly IObjectStore _objects;
    private readonly ITextExtractor _extractor;
    private readonly IEmbeddingClient _embeddings;
    private readonly ChunkingOptions _chunking;

    public IngestionService(
        IRagDatabaseProvider databases,
        IObjectStore objects,
        ITextExtractor extractor,
        IEmbeddingClient embeddings,
        ChunkingOptions? chunking = null)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _chunking = chunking ?? ChunkingOptions.Default;
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
            // Honour cancellation — don't bury it as a document failure.
            throw;
        }
        catch (Exception ex)
        {
            // Fail the document, never the worker (chat-and-tools.md § hardened extraction).
            await TryFailAsync(repo, job.DocumentId, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(
        IngestJob job,
        IRagRepository repo,
        IProgress<IngestionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var document = await repo.GetDocumentAsync(job.DocumentId, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            // Row vanished (deleted mid-flight) — nothing to do.
            return;
        }

        var scope = ObjectScope.Project(job.Iss, job.Sub, job.Pid);

        // 1. Extract (step 1) — read bytes ONLY via the object store.
        ExtractionResult extraction;
        await using (var content = await _objects.OpenReadAsync(scope, job.ObjectKey, cancellationToken).ConfigureAwait(false))
        {
            extraction = await _extractor
                .ExtractAsync(content, job.Extension, cancellationToken)
                .ConfigureAwait(false);
        }

        // 2. No usable text → failed (decisions § 5). Distinguish an unavailable
        //    extractor (carries its own Error) from a text-less file.
        if (!extraction.HasText)
        {
            var error = extraction.Error ?? "no extractable text";
            await FailAsync(repo, document, error, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 3. Chunk (step 3) — token windows with overlap, locator carried through.
        var chunks = TextChunker.Chunk(extraction.Pages, _chunking);
        if (chunks.Count == 0)
        {
            await FailAsync(repo, document, "no extractable text", cancellationToken).ConfigureAwait(false);
            return;
        }

        var total = chunks.Count;
        progress?.Report(new IngestionProgress { ChunksEmbedded = 0, ChunksTotal = total });

        // 4. Embed in batches (step 4) and 5. write (step 5), reporting progress.
        var batchSize = Math.Max(1, _chunking.EmbedBatchSize);
        var embedded = 0;
        for (var offset = 0; offset < total; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = chunks.Skip(offset).Take(batchSize).ToList();
            var vectors = await _embeddings
                .EmbedAsync(batch.Select(c => c.Content).ToList(), cancellationToken)
                .ConfigureAwait(false);

            if (vectors.Count != batch.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding client returned {vectors.Count} vectors for {batch.Count} chunks.");
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
                    Embedding = vectors[i],
                });
            }

            await repo.InsertChunksAsync(inserts, cancellationToken).ConfigureAwait(false);

            embedded += batch.Count;
            progress?.Report(new IngestionProgress { ChunksEmbedded = embedded, ChunksTotal = total });
        }

        // 6. status='ready' + chunk_count.
        await repo.UpdateDocumentAsync(
            document with { Status = Model.DocumentStatus.Ready, ChunkCount = total, Error = null },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task FailAsync(
        IRagRepository repo,
        Model.Rag.Document document,
        string error,
        CancellationToken cancellationToken)
    {
        // Compensate first: chunk batches commit per batch, so a mid-pipeline
        // failure can leave already-inserted chunks behind. Delete them so a
        // failed document leaves nothing retrievable — the row deletion here and
        // HybridSearchAsync's status='ready' join predicate are the two ends of
        // the same guarantee. A no-op when nothing was inserted yet.
        await repo.DeleteChunksAsync(document.Id, cancellationToken).ConfigureAwait(false);

        await repo.UpdateDocumentAsync(
            document with { Status = Model.DocumentStatus.Failed, Error = error, ChunkCount = 0 },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort failure marker for the catch-all path: re-load the row (it may
    /// have advanced) and mark it failed, swallowing any secondary error so the
    /// worker never throws.
    /// </summary>
    private static async Task TryFailAsync(
        IRagRepository repo,
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
        catch
        {
            // The document could not even be marked failed (db gone, cancelled). Nothing
            // more to do — the worker must not throw on the failure path.
        }
    }
}
