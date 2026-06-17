namespace Gert.Chat.OpenAI;

/// <summary>
/// The embeddings functionality (<c>Gert:Embeddings</c>): pick an implementation via
/// <see cref="Type"/>, configure it under <see cref="Parameters"/> - the uniform
/// "functionality -> Type -> Parameters" shape (configuration.md section 4). Only
/// <c>OpenAI</c> ships today (an OpenAI-compatible <c>/v1/embeddings</c> upstream, vLLM
/// serving bge-m3 in the reference deployment); an unknown <see cref="Type"/> fails fast at
/// startup (a registered <c>IValidateOptions</c>). The connection + secret + resilience live
/// in <see cref="Parameters"/>.
/// </summary>
public sealed class EmbeddingsOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Gert:Embeddings";

    /// <summary>The embeddings implementation to use. <c>OpenAI</c> today; unknown fails fast.</summary>
    public string Type { get; set; } = "OpenAI";

    /// <summary>The implementation's connection + resilience (what changes when <see cref="Type"/> changes).</summary>
    public EmbeddingsParameters Parameters { get; set; } = new();
}
