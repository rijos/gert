using System.Text;
using FluentAssertions;
using Gert.Model;
using Gert.Service.Documents;
using Gert.Service.External;
using Gert.Service.Ingestion;
using Gert.Service.Storage;
using Gert.Service.Validation;
using Gert.Storage;
using Gert.Testing;
using Gert.Testing.Fakes;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// End-to-end ingestion tests over a real temp <c>rag.db</c> (with the vendored
/// <c>vec0</c> + FTS5) and a real <see cref="LocalObjectStore"/>: the ingestion
/// pipeline (extract -> chunk -> embed -> write), the document upload/delete path, and
/// memory upsert/delete. All embeddings come from <see cref="FakeEmbeddings"/> so
/// retrieval order is deterministic. Every file byte flows through
/// <see cref="IObjectStore"/> - these tests assert the blob lifecycle there.
/// </summary>
public class IngestionPipelineTests
{
    private readonly FixedUserContext _user = new();
    private string Iss => _user.Iss;
    private string Sub => _user.Sub;

    private const string Pid = "default";

    // ---- ingestion pipeline -----------------------------------------------

    [Fact]
    public async Task Ingest_md_writes_chunks_sets_ready_and_is_retrievable()
    {
        await using var root = new TempDataRoot();
        var harness = await HarnessAsync(root);

        // Upload an .md document; the inline queue runs ingestion synchronously.
        var content =
            "The quarterly report shows revenue grew sharply across every region. "
            + "Widgets remained the strongest product line for the company.";
        var doc = await harness.Documents.UploadAsync(Pid, Upload("report.md", "text/markdown", content));

        var stored = await harness.Documents.GetAsync(Pid, doc.Id);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(DocumentStatus.Ready);
        stored.ChunkCount.Should().BeGreaterThan(0);
        stored.Error.Should().BeNull();

        // Retrievable via hybrid search on the same rag.db.
        await using var repo = await harness.Provider.OpenRagAsync(Iss, Sub, Pid);
        var hits = await repo.HybridSearchAsync("revenue widgets", FakeEmbeddings.Embed("revenue widgets"), k: 5);
        hits.Should().Contain(h => h.Document.Id == doc.Id);
    }

    [Fact]
    public async Task Ingest_emits_progress_culminating_in_all_chunks_embedded()
    {
        await using var root = new TempDataRoot();
        // A recording queue means UploadAsync stores the blob + processing row but does
        // NOT ingest - so the explicit IngestAsync below is the only run and we can
        // capture its IProgress (the inline queue passes none).
        var recording = new RecordingQueue();
        var harness = await HarnessAsync(
            root,
            new ChunkingOptions { MaxTokens = 5, OverlapTokens = 1, EmbedBatchSize = 2 },
            recording);

        var body = string.Join(' ', Enumerable.Range(0, 40).Select(i => $"word{i}"));
        var doc = await harness.Documents.UploadAsync(Pid, Upload("long.txt", "text/plain", body));
        var documentId = doc.Id;

        var reports = new List<IngestionProgress>();
        var progress = new Progress<IngestionProgress>(reports.Add);
        await harness.Ingestion.IngestAsync(
            new IngestJob { Iss = Iss, Sub = Sub, Pid = Pid, DocumentId = documentId, ObjectKey = $"files/{documentId}.txt", Extension = "txt" },
            progress);

        // Progress is observed on the captured SynchronizationContext; give the posts a beat.
        await WaitForAsync(() => reports.Count > 0 && reports[^1].ChunksEmbedded == reports[^1].ChunksTotal);

        reports.Should().NotBeEmpty();
        var last = reports[^1];
        last.ChunksTotal.Should().BeGreaterThan(0);
        last.ChunksEmbedded.Should().Be(last.ChunksTotal);

        await using var repo = await harness.Provider.OpenRagAsync(Iss, Sub, Pid);
        (await repo.GetDocumentAsync(documentId))!.Status.Should().Be(DocumentStatus.Ready);
    }

    [Fact]
    public async Task Ingest_empty_text_marks_document_failed_with_no_extractable_text()
    {
        await using var root = new TempDataRoot();
        var harness = await HarnessAsync(root);

        var doc = await harness.Documents.UploadAsync(Pid, Upload("blank.txt", "text/plain", "   \n\t  "));

        var stored = await harness.Documents.GetAsync(Pid, doc.Id);
        stored!.Status.Should().Be(DocumentStatus.Failed);
        stored.Error.Should().Be("no extractable text");
        stored.ChunkCount.Should().Be(0);
    }

    [Fact]
    public async Task Ingest_pdf_is_deferred_to_U10_and_marks_failed_without_throwing()
    {
        await using var root = new TempDataRoot();
        var harness = await HarnessAsync(root);

        // A .pdf has no extractor in this host -- the doc fails, the worker does not throw.
        var doc = await harness.Documents.UploadAsync(Pid, Upload("scan.pdf", "application/pdf", "%PDF-1.4 fake bytes"));

        var stored = await harness.Documents.GetAsync(Pid, doc.Id);
        stored!.Status.Should().Be(DocumentStatus.Failed);
        stored.Error.Should().Contain("extractor not available");
    }

    [Fact]
    public async Task Ingest_embed_failure_mid_pipeline_leaves_no_retrievable_chunks_and_marks_failed()
    {
        await using var root = new TempDataRoot();
        // Small windows + batch size 2 -> several embed batches; the client succeeds
        // once (so the first batch's chunks ARE committed - batches commit per
        // batch) and then throws, exercising the partial-ingestion failure path.
        var harness = await HarnessAsync(
            root,
            new ChunkingOptions { MaxTokens = 5, OverlapTokens = 1, EmbedBatchSize = 2 },
            embeddings: new FailingEmbeddings(succeedCalls: 1));

        var body = string.Join(' ', Enumerable.Range(0, 40).Select(i => $"word{i}"));
        var doc = await harness.Documents.UploadAsync(Pid, Upload("flaky.txt", "text/plain", body));

        // The worker never throws; the document is failed with zero chunks.
        var stored = await harness.Documents.GetAsync(Pid, doc.Id);
        stored!.Status.Should().Be(DocumentStatus.Failed);
        stored.ChunkCount.Should().Be(0);

        // Nothing is retrievable - "word0" was in the first (committed) batch, so
        // this pins the compensation, not just the absence of later batches.
        await using var repo = await harness.Provider.OpenRagAsync(Iss, Sub, Pid);
        var hits = await repo.HybridSearchAsync("word0", FakeEmbeddings.Embed("word0 word1"), k: 10);
        hits.Should().BeEmpty();

        // Flip the row back to ready to prove the chunk ROWS were deleted - the
        // empty result above must not be only the status filter at work.
        await repo.UpdateDocumentAsync(stored with { Status = DocumentStatus.Ready });
        hits = await repo.HybridSearchAsync("word0", FakeEmbeddings.Embed("word0 word1"), k: 10);
        hits.Should().BeEmpty("the failure path deletes the already-inserted chunks themselves");
    }

    // ---- document upload / delete -----------------------------------------

    [Fact]
    public async Task Upload_streamed_over_the_cap_is_rejected_with_the_validator_error_and_leaves_nothing()
    {
        await using var root = new TempDataRoot();
        // The recording queue would capture any enqueued job; none may appear.
        var recording = new RecordingQueue();
        var harness = await HarnessAsync(root, queue: recording);

        // A streaming caller that cannot know the size up front (SizeBytes null):
        // the validator's size gate is skipped, so the CountingStream cap inside
        // UploadAsync is the only brake (defence-in-depth - both shipped hosts
        // pass a server-measured SizeBytes).
        var upload = new DocumentUpload
        {
            Filename = "huge.txt",
            Mime = "text/plain",
            OpenReadStream = () => new ZeroStream(UploadConstraints.MaxSizeBytes + 1),
            SizeBytes = null,
        };

        var act = async () => await harness.Documents.UploadAsync(Pid, upload);
        var thrown = await act.Should().ThrowAsync<ValidationException>();
        thrown.Which.Result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be("upload.too_large", "the streamed cap surfaces the same 400 as the validator path");

        // No partial blob, no document row, no ingest job.
        var scope = ObjectScope.Project(Iss, Sub, Pid);
        (await harness.Objects.ListAsync(scope, "files/")).Should().BeEmpty("the partial blob is deleted");
        (await harness.Documents.ListAsync(Pid)).Should().BeEmpty();
        recording.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_stores_the_blob_under_the_doc_id_key_and_base64s_the_filename()
    {
        await using var root = new TempDataRoot();
        var harness = await HarnessAsync(root);

        const string original = "Réport (2024) final.md";
        var doc = await harness.Documents.UploadAsync(Pid, Upload(original, "text/markdown", "body text here"));

        // The blob exists under the server-generated {doc-id}.{ext} key - NOT the name.
        var scope = ObjectScope.Project(Iss, Sub, Pid);
        (await harness.Objects.ExistsAsync(scope, $"files/{doc.Id}.md")).Should().BeTrue();

        // documents.filename is base64 of the original; decoding round-trips it exactly.
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(doc.Filename));
        decoded.Should().Be(original);
    }

    [Fact]
    public async Task Upload_enqueues_an_ingest_job()
    {
        await using var root = new TempDataRoot();
        var recording = new RecordingQueue();
        var harness = await HarnessAsync(root, queue: recording);

        var doc = await harness.Documents.UploadAsync(Pid, Upload("note.txt", "text/plain", "hello world"));

        recording.Jobs.Should().ContainSingle();
        var job = recording.Jobs[0];
        job.DocumentId.Should().Be(doc.Id);
        job.ObjectKey.Should().Be($"files/{doc.Id}.txt");
        job.Extension.Should().Be("txt");
        job.Iss.Should().Be(Iss);
        job.Sub.Should().Be(Sub);
        job.Pid.Should().Be(Pid);
    }

    [Fact]
    public async Task Delete_removes_the_rag_rows_and_the_blob()
    {
        await using var root = new TempDataRoot();
        var harness = await HarnessAsync(root);

        var doc = await harness.Documents.UploadAsync(Pid, Upload("gone.md", "text/markdown", "content to delete"));
        var scope = ObjectScope.Project(Iss, Sub, Pid);
        (await harness.Objects.ExistsAsync(scope, $"files/{doc.Id}.md")).Should().BeTrue();

        (await harness.Documents.DeleteAsync(Pid, doc.Id)).Should().BeTrue();

        (await harness.Documents.GetAsync(Pid, doc.Id)).Should().BeNull();
        (await harness.Objects.ExistsAsync(scope, $"files/{doc.Id}.md")).Should().BeFalse("the blob is removed too");

        await using var repo = await harness.Provider.OpenRagAsync(Iss, Sub, Pid);
        var hits = await repo.HybridSearchAsync("content to delete", FakeEmbeddings.Embed("content to delete"), k: 5);
        hits.Should().NotContain(h => h.Document.Id == doc.Id);
    }

    // ---- memory ------------------------------------------------------------

    [Fact]
    public async Task Memory_upsert_stores_the_body_embeds_as_kind_memory_and_is_retrievable()
    {
        await using var root = new TempDataRoot();
        var harness = await HarnessAsync(root);

        var entry = await harness.Memory.UpsertAsync(Pid, new Gert.Model.Dtos.CreateMemoryRequest
        {
            Title = "Preferences",
            Content = "Remember the user prefers concise answers about widgets.",
            Pinned = true,
        });

        // Body blob exists under memory/{id}.md (via the object store).
        var scope = ObjectScope.Project(Iss, Sub, Pid);
        (await harness.Objects.ExistsAsync(scope, $"memory/{entry.Id}.md")).Should().BeTrue();

        // Listed as a memory entry (title round-trips; pinned preserved).
        var listed = await harness.Memory.ListAsync(Pid);
        listed.Should().ContainSingle();
        listed[0].Title.Should().Be("Preferences");
        listed[0].Pinned.Should().BeTrue();

        // Retrievable as kind='memory' alongside documents.
        await using var repo = await harness.Provider.OpenRagAsync(Iss, Sub, Pid);
        var hits = await repo.HybridSearchAsync("widgets", FakeEmbeddings.Embed("widgets concise"), k: 5);
        hits.Should().Contain(h => h.Document.Id == entry.Id && h.Document.Kind == DocumentKind.Memory);
    }

    [Fact]
    public async Task Memory_delete_removes_the_file_and_the_rows()
    {
        await using var root = new TempDataRoot();
        var harness = await HarnessAsync(root);

        var entry = await harness.Memory.UpsertAsync(Pid, new Gert.Model.Dtos.CreateMemoryRequest
        {
            Title = "Throwaway",
            Content = "ephemeral memory body",
        });
        var scope = ObjectScope.Project(Iss, Sub, Pid);
        (await harness.Objects.ExistsAsync(scope, $"memory/{entry.Id}.md")).Should().BeTrue();

        (await harness.Memory.DeleteAsync(Pid, entry.Id)).Should().BeTrue();

        (await harness.Memory.ListAsync(Pid)).Should().BeEmpty();
        (await harness.Objects.ExistsAsync(scope, $"memory/{entry.Id}.md")).Should().BeFalse();
    }

    [Fact]
    public async Task Memory_upsert_embed_failure_leaves_no_row_and_no_blob()
    {
        await using var root = new TempDataRoot();
        // Embedding fails on the very first call: UpsertAsync embeds BEFORE any
        // disk touch, so the failure must leave no document row (no Ready-but-
        // unsearchable entry) and no memory/{id}.md blob behind.
        var harness = await HarnessAsync(root, embeddings: new FailingEmbeddings(succeedCalls: 0));

        var act = async () => await harness.Memory.UpsertAsync(Pid, new Gert.Model.Dtos.CreateMemoryRequest
        {
            Title = "Doomed",
            Content = "this body never gets persisted",
        });

        await act.Should().ThrowAsync<InvalidOperationException>();

        (await harness.Memory.ListAsync(Pid)).Should().BeEmpty("no Ready-but-unsearchable row may survive");
        var scope = ObjectScope.Project(Iss, Sub, Pid);
        (await harness.Objects.ListAsync(scope, "memory/")).Should().BeEmpty("no orphan body blob may survive");
    }

    [Fact]
    public async Task Memory_does_not_appear_in_the_document_list_and_vice_versa()
    {
        await using var root = new TempDataRoot();
        var harness = await HarnessAsync(root);

        var doc = await harness.Documents.UploadAsync(Pid, Upload("d.txt", "text/plain", "a document"));
        var mem = await harness.Memory.UpsertAsync(Pid, new Gert.Model.Dtos.CreateMemoryRequest
        {
            Title = "m",
            Content = "a memory",
        });

        (await harness.Documents.ListAsync(Pid)).Should().ContainSingle().Which.Id.Should().Be(doc.Id);
        (await harness.Memory.ListAsync(Pid)).Should().ContainSingle().Which.Id.Should().Be(mem.Id);
    }

    // ---- harness -----------------------------------------------------------

    private async Task<Harness> HarnessAsync(
        TempDataRoot root,
        ChunkingOptions? chunking = null,
        IIngestionQueue? queue = null,
        IEmbeddingClient? embeddings = null)
    {
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(Iss, Sub);

        var objects = ProviderFixture.ObjectsFor(root);
        embeddings ??= new FakeEmbeddings();
        var extractor = new CompositeTextExtractor(new ITextExtractor[] { new PlainTextExtractor() });
        var ingestion = new IngestionService(provider.Rag, objects, extractor, embeddings, chunking);
        var ingestionQueue = queue ?? new InlineIngestionQueue(ingestion);
        var validation = new PassThroughValidationProvider();

        var documents = new DocumentService(provider.Rag, objects, ingestionQueue, validation, _user, TimeProvider.System);
        var memory = new MemoryService(provider.Rag, objects, embeddings, validation, _user, TimeProvider.System);

        return new Harness(provider, objects, ingestion, documents, memory);
    }

    private static DocumentUpload Upload(string filename, string mime, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new DocumentUpload
        {
            Filename = filename,
            Mime = mime,
            OpenReadStream = () => new MemoryStream(bytes, writable: false),
            SizeBytes = bytes.LongLength,
        };
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    private sealed record Harness(
        ProviderFixture.TestDatabases Provider,
        LocalObjectStore Objects,
        IngestionService Ingestion,
        DocumentService Documents,
        MemoryService Memory);

    private sealed class RecordingQueue : IIngestionQueue
    {
        public List<IngestJob> Jobs { get; } = [];

        public Task EnqueueAsync(IngestJob job, CancellationToken cancellationToken = default)
        {
            Jobs.Add(job);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A failure-injecting <see cref="IEmbeddingClient"/>: delegates the first
    /// <paramref name="succeedCalls"/> calls to <see cref="FakeEmbeddings"/> (so any
    /// committed batches are real, searchable vectors) and throws afterwards. A
    /// local test double, not a second embedding fake - vector semantics still come
    /// from the shared <see cref="FakeEmbeddings"/> spec.
    /// </summary>
    private sealed class FailingEmbeddings(int succeedCalls) : IEmbeddingClient
    {
        private readonly FakeEmbeddings _inner = new();
        private int _calls;

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            if (_calls++ >= succeedCalls)
            {
                throw new InvalidOperationException("embedding backend unavailable (injected test failure)");
            }

            return _inner.EmbedAsync(texts, cancellationToken);
        }
    }

    /// <summary>
    /// A read-only stream of <c>length</c> zero bytes - lets the over-cap upload
    /// test stream 50 MiB + 1 without allocating the payload.
    /// </summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var read = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, read);
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
