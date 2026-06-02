namespace Gert.Service.External;

/// <summary>
/// Port for the OpenAI-compatible chat-completion model (chat-and-tools.md
/// § tool loop). The real client (vLLM over <c>IHttpClientFactory</c> + Polly)
/// lives in <c>Gert.External</c> (U10); tests use a fake. Streaming yields
/// content deltas and tool-call requests; the orchestrator drives the loop.
/// </summary>
public interface IChatModelClient
{
    /// <summary>Stream a completion: text deltas interleaved with tool-call requests.</summary>
    IAsyncEnumerable<ChatModelChunk> StreamAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default);
}
