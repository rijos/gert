using System.Text.Json;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Service.Database;
using Gert.Service.External;

namespace Gert.Service.Tools;

/// <summary>
/// The RAG tool (chat-and-tools.md § RAG / hybrid retrieval). Model function
/// <c>search_documents</c>: embeds the query with <see cref="IEmbeddingClient"/>,
/// runs <see cref="IRagRepository.HybridSearchAsync"/> over <b>this</b> project's
/// <c>rag.db</c> (vector KNN + BM25 fused by RRF), and shapes the hits into a
/// <see cref="ToolResult"/> — a JSON payload for the model plus the
/// <see cref="Citation"/>s that seed the message footnotes.
/// <para>
/// The repository is opened for the caller's validated <c>(iss, sub)</c>
/// (<see cref="IUserContext"/>) and the turn's <c>pid</c>, exactly as
/// <c>ChatService</c> opens chat.db — identity is never caller-supplied, so a
/// query structurally cannot reach another user's or project's documents.
/// </para>
/// </summary>
public sealed class RagTool : ITool
{
    /// <summary>Default top-k when the model omits <c>k</c> (clamped to [1, 20]).</summary>
    private const int DefaultK = 8;
    private const int MinK = 1;
    private const int MaxK = 20;

    private readonly IDatabaseProvider _databases;
    private readonly IEmbeddingClient _embeddings;
    private readonly IUserContext _user;

    public RagTool(IDatabaseProvider databases, IEmbeddingClient embeddings, IUserContext user)
    {
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public string Id => "rag";

    /// <inheritdoc />
    public string Name => "search_documents";

    /// <inheritdoc />
    public string Description =>
        "Hybrid search over this project's private documents and memory. Returns the "
        + "most relevant passages with their source filename, page/section, and score.";

    /// <inheritdoc />
    public string ParametersSchema =>
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
    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        string query;
        int k;
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var root = doc.RootElement;
            query = root.TryGetProperty("query", out var q) ? q.GetString() ?? string.Empty : string.Empty;
            k = root.TryGetProperty("k", out var kv) && kv.TryGetInt32(out var parsed) ? parsed : DefaultK;
        }
        catch (JsonException ex)
        {
            return new ToolResult { Success = false, Error = $"invalid arguments: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolResult { Success = false, Error = "the 'query' argument is required" };
        }

        k = Math.Clamp(k, MinK, MaxK);

        var embeddings = await _embeddings.EmbedAsync([query], cancellationToken).ConfigureAwait(false);
        var queryVector = embeddings.Count > 0 ? embeddings[0] : [];

        await using var repo = await _databases
            .OpenRagAsync(_user.Iss, _user.Sub, invocation.Pid, cancellationToken)
            .ConfigureAwait(false);

        var hits = await repo.HybridSearchAsync(query, queryVector, k, cancellationToken).ConfigureAwait(false);

        return Shape(hits);
    }

    /// <summary>Turn the fused hits into the model-facing JSON plus document citations.</summary>
    private static ToolResult Shape(IReadOnlyList<RetrievedChunk> hits)
    {
        var citations = new List<Citation>(hits.Count);
        var resultHits = new List<object>(hits.Count);

        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var ordinal = i + 1;
            var label = hit.Chunk.Page is { Length: > 0 } page
                ? $"{hit.Document.Filename} · {page}"
                : hit.Document.Filename;

            citations.Add(new Citation
            {
                Id = Guid.NewGuid().ToString("D"),
                MessageId = string.Empty, // bound to the assistant message by ChatService.
                Ordinal = ordinal,
                SourceType = CitationSourceType.Document,
                DocId = hit.Document.Id,
                Label = label,
                Locator = hit.Chunk.Page,
                Score = hit.Score,
            });

            resultHits.Add(new
            {
                ordinal,
                doc = hit.Document.Filename,
                page = hit.Chunk.Page,
                score = Math.Round(hit.Score, 4),
                content = hit.Chunk.Content,
            });
        }

        var resultJson = JsonSerializer.Serialize(new { hits = resultHits });

        return new ToolResult
        {
            Success = true,
            ResultJson = resultJson,
            Citations = citations,
        };
    }
}
