using Gert.Model.Chat;
using Gert.Tools;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The RAG tool (chat-and-tools.md section RAG / hybrid retrieval). Model function
/// <c>search_documents</c>: searches THIS project's documents through the host's pre-scoped
/// <see cref="IRagResource"/> (the host owns the embedding client + the scoped index; vector KNN +
/// BM25 fused by RRF) and shapes the ranked <see cref="RagSearchHit"/>s into a <see cref="RagResult"/>
/// (the JSON payload for the model) plus the <see cref="Citation"/>s that seed the message footnotes.
/// <para>
/// The tool never sees iss/sub/pid and never opens a store or embeds a query - identity is the host's,
/// so a query structurally cannot reach another user's or project's documents.
/// </para>
/// </summary>
public sealed class RagTool : ToolCall<RagArgs, RagResult>
{
    /// <summary>Default top-k when the model omits <c>k</c> (the validator caps the range).</summary>
    private const int DefaultK = 8;

    public RagTool(IValidationProvider validation)
        : base(validation)
    {
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
        "Search this project's private documents; returns the most relevant "
        + "passages with their source and score.";

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
        ArgumentNullException.ThrowIfNull(host);

        var hits = await host.Resources.Rag
            .SearchAsync(RagSearchScope.Project, args.Query, args.K ?? DefaultK, cancellationToken)
            .ConfigureAwait(false);

        return Shape(hits);
    }

    /// <summary>Turn the ranked hits into the model-facing JSON plus document citations.</summary>
    private static ToolCallResult<RagResult> Shape(IReadOnlyList<RagSearchHit> hits)
    {
        var citations = new List<Citation>(hits.Count);
        var resultHits = new List<RagHit>(hits.Count);

        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var ordinal = i + 1;
            var label = hit.Page is { Length: > 0 } page
                ? $"{hit.Title} - {page}"
                : hit.Title;

            citations.Add(new Citation
            {
                Id = Guid.NewGuid().ToString("D"),
                MessageId = string.Empty, // bound to the assistant message by TurnRunner.
                Ordinal = ordinal,
                SourceType = CitationSourceType.Document,
                DocId = hit.DocId,
                Label = label,
                Locator = hit.Page,
                Score = hit.Score,
            });

            resultHits.Add(new RagHit
            {
                Ordinal = ordinal,
                Doc = hit.Title,
                Kind = hit.Kind,
                Page = hit.Page,
                Score = Math.Round(hit.Score, 4),
                Content = hit.Content,
            });
        }

        return ToolCallResult<RagResult>.Ok(new RagResult { Hits = resultHits }, citations: citations);
    }
}
