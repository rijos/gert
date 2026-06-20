using Gert.Chat;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Rag;
using Gert.Rag;
using Gert.Service;
using Gert.Service.Documents;
using Gert.Tools;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The RAG tool (chat-and-tools.md section RAG / hybrid retrieval). Model function
/// <c>search_documents</c>: embeds the query with <see cref="IEmbeddingClient"/>,
/// runs <see cref="IRagStore.HybridSearchAsync"/> over <b>this</b> project's
/// <c>rag.db</c> (vector KNN + BM25 fused by RRF), and shapes the hits into a
/// <see cref="RagResult"/> - a JSON payload for the model plus the
/// <see cref="Citation"/>s that seed the message footnotes.
/// <para>
/// The repository is opened for the caller's validated <c>(iss, sub)</c>
/// (<see cref="IUserContext"/>) and the turn's <c>pid</c>, exactly as the turn
/// pipeline (<c>TurnPlanner</c>/<c>TurnRunner</c>) opens chat.db - identity is
/// never caller-supplied, so a query structurally cannot reach another user's
/// or project's documents.
/// </para>
/// </summary>
public sealed class RagTool : ToolCall<RagArgs, RagResult>
{
    /// <summary>Default top-k when the model omits <c>k</c> (the validator caps the range).</summary>
    private const int DefaultK = 8;

    private readonly IRagIndexProvider _databases;
    private readonly IEmbeddingClient _embeddings;
    private readonly IUserContext _user;

    public RagTool(IValidationProvider validation, IRagIndexProvider databases, IEmbeddingClient embeddings, IUserContext user)
        : base(validation)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public override string Id => "rag";

    /// <inheritdoc />
    public override string Name => "search_documents";

    /// <inheritdoc />
    public override string Description =>
        // Lean by design: qwen-class models lose tool-call format adherence when
        // the rendered tools block grows past ~1.8k tokens (chat-and-tools.md
        // section tool specs are a token budget) - keep every description to one or
        // two short sentences that carry only the behavioural contract.
        "Search this project's private documents and memory; returns the most "
        + "relevant passages with their source and score.";

    /// <inheritdoc />
    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "The natural-language search query." },
            "k": { "type": "integer", "description": "How many passages to return (1-20, default 8).", "minimum": 1, "maximum": 20 }
          },
          "required": ["query"]
        }
        """;

    /// <inheritdoc />
    public override async Task<ToolCallResult<RagResult>> CallAsync(
        RagArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);

        var query = args.Query;
        var k = args.K ?? DefaultK;

        var embeddings = await _embeddings.EmbedAsync([query], cancellationToken).ConfigureAwait(false);
        if (embeddings.Count != 1)
        {
            // Contract violation by the embedding client (one vector per input
            // text): fail the call with an error the model can read - silently
            // searching with an empty vector would degrade to BM25-only results
            // with no signal that vector recall was lost.
            return ToolCallResult<RagResult>.Fail(
                $"embedding failed: expected 1 query vector, got {embeddings.Count}");
        }

        var queryVector = embeddings[0];

        await using var repo = await _databases
            .OpenAsync(_user.Iss, _user.Sub, invocation.Pid, cancellationToken)
            .ConfigureAwait(false);

        var hits = await repo.HybridSearchAsync(query, queryVector, k, cancellationToken).ConfigureAwait(false);

        return Shape(hits);
    }

    /// <summary>Turn the fused hits into the model-facing JSON plus document citations.</summary>
    private static ToolCallResult<RagResult> Shape(IReadOnlyList<RetrievedChunk> hits)
    {
        var citations = new List<Citation>(hits.Count);
        var resultHits = new List<RagHit>(hits.Count);

        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var ordinal = i + 1;
            // A memory hit's `filename` is the base64-encoded entry title
            // (MemoryService.EncodeTitle) - decode it so the card and the
            // citation show the title, never the encoded blob.
            var display = DisplayName(hit.Document);
            var label = hit.Chunk.Page is { Length: > 0 } page
                ? $"{display} - {page}"
                : display;

            citations.Add(new Citation
            {
                Id = Guid.NewGuid().ToString("D"),
                MessageId = string.Empty, // bound to the assistant message by TurnRunner.
                Ordinal = ordinal,
                SourceType = CitationSourceType.Document,
                DocId = hit.Document.Id,
                Label = label,
                Locator = hit.Chunk.Page,
                Score = hit.Score,
            });

            resultHits.Add(new RagHit
            {
                Ordinal = ordinal,
                Doc = display,
                Kind = hit.Document.Kind == DocumentKind.Memory ? "memory" : "document",
                Page = hit.Chunk.Page,
                Score = Math.Round(hit.Score, 4),
                Content = hit.Chunk.Content,
            });
        }

        return ToolCallResult<RagResult>.Ok(new RagResult { Hits = resultHits }, citations: citations);
    }

    /// <summary>
    /// What a hit is called in the card/citation. The <c>filename</c> column is
    /// base64 display metadata for EVERY kind - uploads through
    /// <c>StoredFilenames.Encode</c>, memory titles through
    /// <c>MemoryService.EncodeTitle</c> - so always decode (the old
    /// memory-only decode leaked base64 labels for document hits; found live
    /// 2026-06-11). Falls back to the raw value if it does not decode
    /// (defensive; never fails the search).
    /// </summary>
    private static string DisplayName(Document document) =>
        StoredFilenames.Decode(document.Filename);
}
