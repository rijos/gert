using System.Buffers.Binary;
using System.Globalization;
using Dapper;
using Gert.Model;
using Gert.Model.Rag;
using Microsoft.Data.Sqlite;

namespace Gert.Rag.Sqlite;

/// <summary>
/// Dapper-backed <see cref="IRagStore"/> over one project's <c>rag.db</c>
/// (storage-and-data.md section rag.db). Wraps a single open connection - opened by the
/// provider with the native <b>sqlite-vec</b> extension already loaded - whose
/// path is the scope, so a query cannot reach another project's rows.
///
/// <para>
/// Memory and documents share the <c>documents</c> table, distinguished by
/// <c>kind</c>; both are retrieved together by <see cref="HybridSearchAsync"/>
/// (chat-and-tools.md section "memory rides the same query"). The three indexes share an
/// integer rowid: <c>chunks.id</c> == <c>vec_chunks.chunk_id</c> == the
/// <c>fts_chunks</c> rowid, so they join cheaply.
/// </para>
///
/// <para>
/// Hybrid retrieval fuses a vector KNN list and a BM25 list with Reciprocal Rank
/// Fusion (RRF, constant <see cref="RrfK"/>): each list contributes
/// <c>1 / (RrfK + rank)</c> per chunk, summed across lists, and the top-<c>k</c>
/// by fused score are returned (chat-and-tools.md section hybrid retrieval).
/// </para>
/// </summary>
public sealed class SqliteRagStore : IRagStore
{
    /// <summary>The RRF rank-bias constant (the conventional 60).</summary>
    private const int RrfK = 60;

    /// <summary>The embedding dimension - must match <c>vec0(FLOAT[1024])</c> (bge-m3).</summary>
    private const int EmbeddingDimensions = 1024;

    static SqliteRagStore()
    {
        // Process-wide Dapper config - this engine leaf's owner (RagDapperBootstrap).
        RagDapperBootstrap.EnsureConfigured();
    }

    private readonly SqliteConnection _connection;

    /// <summary>Wrap an open <c>rag.db</c> connection (sqlite-vec already loaded).</summary>
    public SqliteRagStore(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Document>> ListDocumentsAsync(
        DocumentKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        var sql =
            "SELECT id, filename, mime, size_bytes, status, chunk_count, error, kind, pinned, created_at " +
            "FROM documents" +
            (kind is null ? string.Empty : " WHERE kind = @kind") +
            " ORDER BY created_at DESC, id ASC;";

        var rows = await _connection.QueryAsync<DocumentRow>(
            new CommandDefinition(
                sql,
                kind is null ? null : new { kind = KindToString(kind.Value) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(MapDocument).ToList();
    }

    /// <inheritdoc />
    public async Task<Document?> GetDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        const string sql =
            "SELECT id, filename, mime, size_bytes, status, chunk_count, error, kind, pinned, created_at " +
            "FROM documents WHERE id = @id;";

        var row = await _connection.QuerySingleOrDefaultAsync<DocumentRow>(
            new CommandDefinition(sql, new { id = documentId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : MapDocument(row);
    }

    /// <inheritdoc />
    public async Task InsertDocumentAsync(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        const string sql =
            "INSERT INTO documents (id, filename, mime, size_bytes, status, chunk_count, error, kind, pinned, created_at) " +
            "VALUES (@Id, @Filename, @Mime, @SizeBytes, @Status, @ChunkCount, @Error, @Kind, @Pinned, @CreatedAt);";

        await _connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            document.Id,
            document.Filename,
            document.Mime,
            document.SizeBytes,
            Status = StatusToString(document.Status),
            document.ChunkCount,
            document.Error,
            Kind = KindToString(document.Kind),
            Pinned = document.Pinned ? 1 : 0,
            CreatedAt = FormatTime(document.CreatedAt),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateDocumentAsync(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        const string sql =
            "UPDATE documents SET filename = @Filename, mime = @Mime, size_bytes = @SizeBytes, " +
            "status = @Status, chunk_count = @ChunkCount, error = @Error, kind = @Kind, pinned = @Pinned " +
            "WHERE id = @Id;";

        await _connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            document.Id,
            document.Filename,
            document.Mime,
            document.SizeBytes,
            Status = StatusToString(document.Status),
            document.ChunkCount,
            document.Error,
            Kind = KindToString(document.Kind),
            Pinned = document.Pinned ? 1 : 0,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The vec0 / fts5 virtual tables are not reached by FK ON DELETE CASCADE,
        // so clear their rows explicitly for this document's chunks first, then the
        // chunks (cascades from documents would clear chunks, but we drop the doc
        // last so the chunk ids are still resolvable here).
        await DeleteChunkSatellitesAsync(
            "SELECT id FROM chunks WHERE document_id = @documentId",
            new { documentId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await _connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM chunks WHERE document_id = @documentId;",
            new { documentId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var affected = await _connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM documents WHERE id = @documentId;",
            new { documentId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await DeleteChunkSatellitesAsync(
            "SELECT id FROM chunks",
            null,
            transaction,
            cancellationToken).ConfigureAwait(false);

        await _connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM chunks;", transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        await _connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM documents;", transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteChunksAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Same shape as DeleteDocumentAsync minus the document row: satellites
        // first (the vec0 / fts5 virtual tables are not reached by FK cascades),
        // then the chunks, all in one transaction so a fault never leaves an
        // index row pointing at a deleted chunk.
        await DeleteChunkSatellitesAsync(
            "SELECT id FROM chunks WHERE document_id = @documentId",
            new { documentId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await _connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM chunks WHERE document_id = @documentId;",
            new { documentId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task InsertChunksAsync(
        IReadOnlyList<ChunkInsert> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            return;
        }

        const string chunkSql =
            "INSERT INTO chunks (document_id, ordinal, content, page, token_count) " +
            "VALUES (@DocumentId, @Ordinal, @Content, @Page, @TokenCount) RETURNING id;";

        // vec0 accepts the embedding as a packed little-endian float32 BLOB; the
        // fts row carries the matching rowid so chunks.id links all three tables.
        const string vecSql =
            "INSERT INTO vec_chunks (chunk_id, embedding) VALUES (@id, @embedding);";
        const string ftsSql =
            "INSERT INTO fts_chunks (rowid, content) VALUES (@id, @content);";

        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var chunk in chunks)
        {
            var id = await _connection.ExecuteScalarAsync<long>(new CommandDefinition(chunkSql, new
            {
                chunk.DocumentId,
                chunk.Ordinal,
                chunk.Content,
                chunk.Page,
                chunk.TokenCount,
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await _connection.ExecuteAsync(new CommandDefinition(vecSql, new
            {
                id,
                embedding = PackEmbedding(chunk.Embedding),
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await _connection.ExecuteAsync(new CommandDefinition(ftsSql, new
            {
                id,
                content = chunk.Content,
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedChunk>> HybridSearchAsync(
        string query,
        IReadOnlyList<float> queryEmbedding,
        int k,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        if (k <= 0)
        {
            return Array.Empty<RetrievedChunk>();
        }

        const string vecSql =
            "SELECT chunk_id, distance FROM vec_chunks " +
            "WHERE embedding MATCH @qvec ORDER BY distance LIMIT @k;";
        var vecHits = (await _connection.QueryAsync<VecHit>(new CommandDefinition(
            vecSql,
            new { qvec = PackEmbedding(queryEmbedding), k },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        // The match string is carried as a single quoted FTS5 string so operators/
        // quotes in the user's query are data, not syntax (avoids FTS-syntax
        // injection). bm25() is lower = better.
        const string ftsSql =
            "SELECT rowid AS chunk_id, bm25(fts_chunks) AS score FROM fts_chunks " +
            "WHERE fts_chunks MATCH @q ORDER BY score LIMIT @k;";
        var ftsHits = (await _connection.QueryAsync<FtsHit>(new CommandDefinition(
            ftsSql,
            new { q = EscapeFtsQuery(query), k },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        // Reciprocal Rank Fusion: each list contributes 1 / (RrfK + rank), rank
        // being the 1-based position within that already-sorted list.
        var fused = new Dictionary<long, double>();
        AccumulateRrf(fused, vecHits.Select(h => h.ChunkId));
        AccumulateRrf(fused, ftsHits.Select(h => h.ChunkId));

        var top = fused
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key) // deterministic tie-break by chunk id
            .Take(k)
            .ToList();

        if (top.Count == 0)
        {
            return Array.Empty<RetrievedChunk>();
        }

        // Only status='ready' documents are retrievable (literal matches
        // StatusToString): a still-processing document's chunks exist transiently
        // (batches commit per batch) and a failed document's chunks are deleted by
        // the ingestion failure path - this predicate is the read-side guarantee.
        // The loop below tolerates ids dropped by the join.
        const string joinSql =
            "SELECT c.id, c.document_id, c.ordinal, c.content, c.page, c.token_count, " +
            "       d.id AS d_id, d.filename, d.mime, d.size_bytes, d.status, d.chunk_count, " +
            "       d.error, d.kind, d.pinned, d.created_at " +
            "FROM chunks c JOIN documents d ON d.id = c.document_id " +
            "WHERE c.id IN @ids AND d.status = 'ready';";
        var rows = await _connection.QueryAsync<RetrievedRow>(new CommandDefinition(
            joinSql,
            new { ids = top.Select(p => p.Key).ToArray() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var byId = rows.ToDictionary(r => r.Id);
        var results = new List<RetrievedChunk>(top.Count);
        foreach (var (chunkId, score) in top)
        {
            if (!byId.TryGetValue(chunkId, out var row))
            {
                continue;
            }

            results.Add(new RetrievedChunk
            {
                Chunk = new Chunk
                {
                    Id = row.Id,
                    DocumentId = row.DocumentId,
                    Ordinal = row.Ordinal,
                    Content = row.Content,
                    Page = row.Page,
                    TokenCount = row.TokenCount,
                },
                Document = new Document
                {
                    Id = row.DId,
                    Filename = row.Filename,
                    Mime = row.Mime,
                    SizeBytes = row.SizeBytes,
                    Status = StatusFromString(row.Status),
                    ChunkCount = row.ChunkCount,
                    Error = row.Error,
                    Kind = KindFromString(row.Kind),
                    Pinned = row.Pinned != 0,
                    CreatedAt = ParseTime(row.CreatedAt),
                },
                Score = score,
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private static void AccumulateRrf(Dictionary<long, double> fused, IEnumerable<long> rankedIds)
    {
        var rank = 1;
        foreach (var id in rankedIds)
        {
            fused[id] = fused.GetValueOrDefault(id) + (1.0 / (RrfK + rank));
            rank++;
        }
    }

    /// <summary>Pack a vector as little-endian float32 bytes for the vec0 BLOB binding.</summary>
    private static byte[] PackEmbedding(IReadOnlyList<float> embedding)
    {
        if (embedding.Count != EmbeddingDimensions)
        {
            throw new ArgumentException(
                $"Embedding has {embedding.Count} dimensions; expected {EmbeddingDimensions} (FLOAT[{EmbeddingDimensions}]).",
                nameof(embedding));
        }

        var bytes = new byte[embedding.Count * sizeof(float)];
        for (var i = 0; i < embedding.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), embedding[i]);
        }

        return bytes;
    }

    /// <summary>
    /// Quote a user query into a single FTS5 string token. Wrapping in double
    /// quotes (and doubling any internal double-quote) makes the whole query a
    /// literal phrase, so FTS5 operators (<c>AND</c>, <c>NEAR</c>, <c>*</c>,
    /// <c>"</c>, <c>(</c>, ...) are matched as data, never parsed as syntax.
    /// </summary>
    private static string EscapeFtsQuery(string query) =>
        "\"" + query.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private async Task DeleteChunkSatellitesAsync(
        string idSelectSql,
        object? parameters,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var ids = (await _connection.QueryAsync<long>(new CommandDefinition(
            idSelectSql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();

        if (ids.Count == 0)
        {
            return;
        }

        // Delete per id (by primary key) - robust across the vec0 / fts5 virtual
        // tables, which do not implement FK ON DELETE CASCADE.
        //
        // vec_chunks: a plain DELETE by chunk_id.
        // fts_chunks (external-content): the special 'delete' command, which needs
        //   the original rowid + content to remove the entry from the inverted index.
        const string vecDeleteSql = "DELETE FROM vec_chunks WHERE chunk_id = @id;";
        const string ftsDeleteSql =
            "INSERT INTO fts_chunks (fts_chunks, rowid, content) " +
            "SELECT 'delete', id, content FROM chunks WHERE id = @id;";
        foreach (var id in ids)
        {
            await _connection.ExecuteAsync(new CommandDefinition(
                vecDeleteSql, new { id }, transaction, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            await _connection.ExecuteAsync(new CommandDefinition(
                ftsDeleteSql, new { id }, transaction, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    private static Document MapDocument(DocumentRow row) => new()
    {
        Id = row.Id,
        Filename = row.Filename,
        Mime = row.Mime,
        SizeBytes = row.SizeBytes,
        Status = StatusFromString(row.Status),
        ChunkCount = row.ChunkCount,
        Error = row.Error,
        Kind = KindFromString(row.Kind),
        Pinned = row.Pinned != 0,
        CreatedAt = ParseTime(row.CreatedAt),
    };

    private static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string StatusToString(DocumentStatus status) => status switch
    {
        DocumentStatus.Processing => "processing",
        DocumentStatus.Ready => "ready",
        DocumentStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static DocumentStatus StatusFromString(string value) => value switch
    {
        "processing" => DocumentStatus.Processing,
        "ready" => DocumentStatus.Ready,
        "failed" => DocumentStatus.Failed,
        _ => throw new InvalidOperationException($"Unknown document status '{value}'."),
    };

    private static string KindToString(DocumentKind kind) => kind switch
    {
        DocumentKind.Document => "document",
        DocumentKind.Memory => "memory",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static DocumentKind KindFromString(string value) => value switch
    {
        "document" => DocumentKind.Document,
        "memory" => DocumentKind.Memory,
        _ => throw new InvalidOperationException($"Unknown document kind '{value}'."),
    };

    // PascalCase properties; Dapper binds snake_case columns via
    // MatchNamesWithUnderscores and narrows SQLite's Int64 to each property type.

    private sealed record DocumentRow
    {
        public required string Id { get; init; }
        public required string Filename { get; init; }
        public required string Mime { get; init; }
        public long SizeBytes { get; init; }
        public required string Status { get; init; }
        public int ChunkCount { get; init; }
        public string? Error { get; init; }
        public required string Kind { get; init; }
        public int Pinned { get; init; }
        public required string CreatedAt { get; init; }
    }

    private sealed record VecHit
    {
        public long ChunkId { get; init; }
        public double Distance { get; init; }
    }

    private sealed record FtsHit
    {
        public long ChunkId { get; init; }
        public double Score { get; init; }
    }

    private sealed record RetrievedRow
    {
        public long Id { get; init; }
        public required string DocumentId { get; init; }
        public int Ordinal { get; init; }
        public required string Content { get; init; }
        public string? Page { get; init; }
        public int? TokenCount { get; init; }
        public required string DId { get; init; }
        public required string Filename { get; init; }
        public required string Mime { get; init; }
        public long SizeBytes { get; init; }
        public required string Status { get; init; }
        public int ChunkCount { get; init; }
        public string? Error { get; init; }
        public required string Kind { get; init; }
        public int Pinned { get; init; }
        public required string CreatedAt { get; init; }
    }
}
