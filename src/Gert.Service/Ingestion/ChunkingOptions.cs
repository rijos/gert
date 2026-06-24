namespace Gert.Service.Ingestion;

/// <summary>
/// Token-window settings for ingestion step 3 (chat-and-tools.md section ingestion:
/// "token-aware windows w/ overlap"). Tokens are approximated by whitespace-split
/// words - deterministic and dependency-free; a later change may swap in the model tokenizer
/// without changing the pipeline. A window of <see cref="MaxTokens"/> words slides
/// forward by <c>MaxTokens - OverlapTokens</c>, so consecutive chunks share
/// <see cref="OverlapTokens"/> words of context for retrieval continuity.
/// </summary>
public sealed record ChunkingOptions
{
    /// <summary>Maximum approximate tokens (words) per chunk.</summary>
    public int MaxTokens { get; init; } = 256;

    /// <summary>Tokens (words) of overlap carried between consecutive chunks.</summary>
    public int OverlapTokens { get; init; } = 32;

    /// <summary>Batch size for the embedding calls (step 4).</summary>
    public int EmbedBatchSize { get; init; } = 16;

    public static ChunkingOptions Default { get; } = new();
}
