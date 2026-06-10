using FluentAssertions;
using Gert.Database.Sqlite;
using Gert.Model;
using Gert.Model.Rag;
using Gert.Database;
using Gert.Testing;
using Gert.Testing.Fakes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// Real temp <c>rag.db</c> with the real vendored <c>vec0</c> extension and FTS5.
/// Exercises provisioning, chunk writes, vector KNN, BM25, RRF fusion, memory
/// retrieval, cascade delete, project isolation, and FTS-injection safety. All
/// vectors come from <see cref="FakeEmbeddings"/> so KNN/RRF order is deterministic.
/// </summary>
public class SqliteRagRepositoryTests
{
    private const string Iss = ProviderFixture.ExpectedIssuer;
    private const string Sub = "rag-sub";

    [Fact]
    public async Task Rag_migration_creates_vec_and_fts_tables_and_db_exists()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);

        await provider.EnsureProvisionedAsync(Iss, Sub);

        var ragDb = paths.RagDb(Iss, Sub, "default");
        File.Exists(ragDb).Should().BeTrue("provisioning must create rag.db");

        await using var repo = await provider.OpenRagAsync(Iss, Sub, "default");
        var tables = await TablesAndColumnsAsync(paths);

        tables.Should().Contain("documents");
        tables.Should().Contain("chunks");
        tables.Should().Contain("vec_chunks");
        tables.Should().Contain("fts_chunks");
    }

    [Fact]
    public async Task Document_and_memory_round_trip_and_list_filters_by_kind()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await using var repo = await provider.OpenRagAsync(Iss, Sub, "default");

        var doc = NewDocument("doc.pdf", DocumentKind.Document);
        var mem = NewDocument("note", DocumentKind.Memory) with { Pinned = true };
        await repo.InsertDocumentAsync(doc);
        await repo.InsertDocumentAsync(mem);

        (await repo.GetDocumentAsync(doc.Id))!.Filename.Should().Be("doc.pdf");
        var memLoaded = await repo.GetDocumentAsync(mem.Id);
        memLoaded!.Kind.Should().Be(DocumentKind.Memory);
        memLoaded.Pinned.Should().BeTrue();

        (await repo.ListDocumentsAsync(DocumentKind.Document)).Should().ContainSingle()
            .Which.Id.Should().Be(doc.Id);
        (await repo.ListDocumentsAsync(DocumentKind.Memory)).Should().ContainSingle()
            .Which.Id.Should().Be(mem.Id);
        (await repo.ListDocumentsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Vector_knn_returns_nearest_by_query_embedding_in_deterministic_order()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await using var repo = await provider.OpenRagAsync(Iss, Sub, "default");

        var doc = NewDocument("vectors.txt", DocumentKind.Document);
        await repo.InsertDocumentAsync(doc);

        // Distinct strings → near-orthogonal unit vectors. The query embedding is
        // exactly the "target" vector, so that chunk must be the nearest.
        await repo.InsertChunksAsync(new[]
        {
            Chunk(doc.Id, 0, "alpha-unrelated"),
            Chunk(doc.Id, 1, "the-target-chunk"),
            Chunk(doc.Id, 2, "gamma-unrelated"),
        });

        // A query with no lexical overlap isolates the vector path: FTS contributes
        // nothing, so RRF order == vector order.
        var hits = await repo.HybridSearchAsync(
            "zzznolexicalmatchzzz",
            FakeEmbeddings.Embed("the-target-chunk"),
            k: 3);

        hits.Should().NotBeEmpty();
        hits[0].Chunk.Content.Should().Be("the-target-chunk");
    }

    [Fact]
    public async Task Fts_bm25_returns_lexical_hit()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await using var repo = await provider.OpenRagAsync(Iss, Sub, "default");

        var doc = NewDocument("text.txt", DocumentKind.Document);
        await repo.InsertDocumentAsync(doc);
        await repo.InsertChunksAsync(new[]
        {
            Chunk(doc.Id, 0, "quarterly revenue grew sharply"),
            Chunk(doc.Id, 1, "the cat sat on the mat"),
        });

        // Query embedding is orthogonal to both chunks; only BM25 can surface a hit.
        var hits = await repo.HybridSearchAsync(
            "revenue",
            FakeEmbeddings.Embed("orthogonal-query-vector"),
            k: 2);

        hits.Should().Contain(h => h.Chunk.Content == "quarterly revenue grew sharply");
    }

    [Fact]
    public async Task Rrf_fuses_disagreeing_vector_and_lexical_rankings()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await using var repo = await provider.OpenRagAsync(Iss, Sub, "default");

        var doc = NewDocument("fusion.txt", DocumentKind.Document);
        await repo.InsertDocumentAsync(doc);

        // A is the vector winner; B is the lexical winner.
        // Vector list (query == Embed(A.content)): [A rank1, B rank2].
        // FTS list (query == "fusionword", present only in B): [B rank1].
        // RRF: A = 1/(60+1) = 0.01639; B = 1/(60+2) + 1/(60+1) = 0.01613 + 0.01639
        //      = 0.03252.  B outranks A even though A is the vector winner.
        const string contentA = "alpha vector winner chunk";
        const string contentB = "beta fusionword lexical winner chunk";
        await repo.InsertChunksAsync(new[]
        {
            Chunk(doc.Id, 0, contentA),
            Chunk(doc.Id, 1, contentB),
        });

        var hits = await repo.HybridSearchAsync(
            "fusionword",
            FakeEmbeddings.Embed(contentA),
            k: 2);

        hits.Should().HaveCount(2);
        hits[0].Chunk.Content.Should().Be(contentB, "RRF promotes the chunk that scores in both lists");
        hits[1].Chunk.Content.Should().Be(contentA);
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
    }

    [Fact]
    public async Task Memory_chunks_are_retrieved_alongside_documents()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await using var repo = await provider.OpenRagAsync(Iss, Sub, "default");

        var doc = NewDocument("doc.txt", DocumentKind.Document);
        var mem = NewDocument("memory-note", DocumentKind.Memory);
        await repo.InsertDocumentAsync(doc);
        await repo.InsertDocumentAsync(mem);

        await repo.InsertChunksAsync(new[] { Chunk(doc.Id, 0, "document body about widgets") });
        await repo.InsertChunksAsync(new[] { Chunk(mem.Id, 0, "remember the widgets preference") });

        // Lexical "widgets" hits both; both kinds must be retrievable in one query.
        var hits = await repo.HybridSearchAsync(
            "widgets",
            FakeEmbeddings.Embed("remember the widgets preference"),
            k: 5);

        hits.Select(h => h.Document.Kind).Should().Contain(DocumentKind.Document);
        hits.Select(h => h.Document.Kind).Should().Contain(DocumentKind.Memory);
    }

    [Fact]
    public async Task Hybrid_search_returns_only_chunks_of_ready_documents()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await using var repo = await provider.OpenRagAsync(Iss, Sub, "default");

        // One document per status; every chunk shares the lexical token AND the
        // embeddings are close enough for k=10 KNN to surface all three — only the
        // ready one may come back (failed/processing chunks must never leak into
        // retrieval, even while their rows exist).
        var ready = NewDocument("ready.txt", DocumentKind.Document);
        var processing = NewDocument("processing.txt", DocumentKind.Document) with { Status = DocumentStatus.Processing };
        var failed = NewDocument("failed.txt", DocumentKind.Document) with { Status = DocumentStatus.Failed };
        await repo.InsertDocumentAsync(ready);
        await repo.InsertDocumentAsync(processing);
        await repo.InsertDocumentAsync(failed);

        await repo.InsertChunksAsync(new[] { Chunk(ready.Id, 0, "statusword from the ready document") });
        await repo.InsertChunksAsync(new[] { Chunk(processing.Id, 0, "statusword from the processing document") });
        await repo.InsertChunksAsync(new[] { Chunk(failed.Id, 0, "statusword from the failed document") });

        var hits = await repo.HybridSearchAsync(
            "statusword",
            FakeEmbeddings.Embed("statusword from the ready document"),
            k: 10);

        hits.Should().NotBeEmpty("the ready document's chunk is retrievable");
        hits.Should().OnlyContain(h => h.Document.Id == ready.Id, "non-ready documents' chunks must be filtered out");
    }

    [Fact]
    public async Task Delete_chunks_removes_chunk_vec_and_fts_rows_and_keeps_the_document()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);
        await using var repo = await provider.OpenRagAsync(Iss, Sub, "default");

        var doc = NewDocument("kept.txt", DocumentKind.Document);
        var other = NewDocument("other.txt", DocumentKind.Document);
        await repo.InsertDocumentAsync(doc);
        await repo.InsertDocumentAsync(other);
        await repo.InsertChunksAsync(new[]
        {
            Chunk(doc.Id, 0, "compensated chunk one"),
            Chunk(doc.Id, 1, "compensated chunk two"),
        });
        await repo.InsertChunksAsync(new[] { Chunk(other.Id, 0, "untouched sibling chunk") });

        await repo.DeleteChunksAsync(doc.Id);

        // The document row survives (the ingestion failure path flips it to
        // failed separately); only ITS chunks + satellites are gone.
        (await repo.GetDocumentAsync(doc.Id)).Should().NotBeNull();
        var counts = await CountsAsync(paths);
        counts.Chunks.Should().Be(1, "the other document's chunk is untouched");
        counts.Vec.Should().Be(1);
        counts.Fts.Should().Be(1);

        // Vector KNN always returns the nearest remaining chunks regardless of
        // distance, so the sibling's chunk may legitimately surface — the guarantee
        // is that nothing from the deleted document does.
        var hits = await repo.HybridSearchAsync(
            "compensated",
            FakeEmbeddings.Embed("compensated chunk one"),
            k: 5);
        hits.Should().NotContain(
            h => h.Document.Id == doc.Id,
            "deleted chunks must not be retrievable");
    }

    [Fact]
    public async Task Delete_document_removes_its_chunks_vec_and_fts_rows()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        var paths = ProviderFixture.PathsFor(root);
        await using var repo = await provider.OpenRagAsync(Iss, Sub, "default");

        var doc = NewDocument("gone.txt", DocumentKind.Document);
        await repo.InsertDocumentAsync(doc);
        await repo.InsertChunksAsync(new[]
        {
            Chunk(doc.Id, 0, "deletable chunk one"),
            Chunk(doc.Id, 1, "deletable chunk two"),
        });

        (await repo.DeleteDocumentAsync(doc.Id)).Should().BeTrue();
        (await repo.GetDocumentAsync(doc.Id)).Should().BeNull();

        // Verify the satellite tables are empty too (no orphaned vec/fts rows).
        var counts = await CountsAsync(paths);
        counts.Chunks.Should().Be(0);
        counts.Vec.Should().Be(0);
        counts.Fts.Should().Be(0);

        // And a query no longer surfaces anything.
        var hits = await repo.HybridSearchAsync(
            "deletable",
            FakeEmbeddings.Embed("deletable chunk one"),
            k: 5);
        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Projects_are_isolated_query_in_one_cannot_see_the_other()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(Iss, Sub);

        var projectB = Guid.NewGuid().ToString("D");
        await provider.EnsureProjectAsync(Iss, Sub, projectB);

        // Write a unique chunk only into the default project.
        await using (var repoA = await provider.OpenRagAsync(Iss, Sub, "default"))
        {
            var doc = NewDocument("a.txt", DocumentKind.Document);
            await repoA.InsertDocumentAsync(doc);
            await repoA.InsertChunksAsync(new[] { Chunk(doc.Id, 0, "secretfromprojecta") });
        }

        await using var repoB = await provider.OpenRagAsync(Iss, Sub, projectB);
        var hits = await repoB.HybridSearchAsync(
            "secretfromprojecta",
            FakeEmbeddings.Embed("secretfromprojecta"),
            k: 5);

        hits.Should().BeEmpty("project B's rag.db is a separate file");
    }

    [Fact]
    public async Task Fts_operators_in_the_query_are_treated_as_data_not_syntax()
    {
        await using var root = new TempDataRoot();
        var provider = ProviderFixture.ProviderFor(root);
        await using var repo = await provider.OpenRagAsync(Iss, Sub, "default");

        var doc = NewDocument("safe.txt", DocumentKind.Document);
        await repo.InsertDocumentAsync(doc);
        await repo.InsertChunksAsync(new[] { Chunk(doc.Id, 0, "ordinary content here") });

        // These contain raw FTS5 operators/quotes; a naive bind would raise a
        // "fts5: syntax error" — escaping must make them harmless data.
        foreach (var malicious in new[]
        {
            "foo\" OR \"bar",
            "a AND b NEAR(c)",
            "unbalanced \" quote",
            "wild*card AND (group)",
            "\"\"\"",
        })
        {
            var act = async () => await repo.HybridSearchAsync(
                malicious, FakeEmbeddings.Embed("ordinary content here"), k: 3);

            await act.Should().NotThrowAsync($"query '{malicious}' must be treated as data");
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static Document NewDocument(string filename, DocumentKind kind) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Filename = filename,
        Mime = kind == DocumentKind.Memory ? "text/markdown" : "text/plain",
        SizeBytes = 1234,
        Status = DocumentStatus.Ready,
        ChunkCount = 0,
        Kind = kind,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static ChunkInsert Chunk(string documentId, int ordinal, string content) => new()
    {
        DocumentId = documentId,
        Ordinal = ordinal,
        Content = content,
        Page = $"p.{ordinal + 1}",
        TokenCount = content.Length,
        Embedding = FakeEmbeddings.Embed(content),
    };

    private static async Task<IReadOnlyList<string>> TablesAndColumnsAsync(SqliteDatabasePaths paths)
    {
        await using var connection = await OpenRagDirectAsync(paths.RagDb(Iss, Sub, "default"));
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table');";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<(long Chunks, long Vec, long Fts)> CountsAsync(SqliteDatabasePaths paths)
    {
        await using var connection = await OpenRagDirectAsync(paths.RagDb(Iss, Sub, "default"));
        var chunks = await ScalarAsync(connection, "SELECT count(*) FROM chunks;");
        var vec = await ScalarAsync(connection, "SELECT count(*) FROM vec_chunks;");
        var fts = await ScalarAsync(connection, "SELECT count(*) FROM fts_chunks;");
        return (chunks, vec, fts);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<SqliteConnection> OpenRagDirectAsync(string dbPath)
    {
        var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        connection.EnableExtensions(true);
        connection.LoadExtension(Path.Combine(AppContext.BaseDirectory, "vec0.so"));
        return connection;
    }
}
