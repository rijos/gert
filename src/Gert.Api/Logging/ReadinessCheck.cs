using Gert.Chat.OpenAI;
using Gert.Tools.Search;
using Gert.Tools.Search.SearXNG;

namespace Gert.Api.Logging;

/// <summary>
/// Best-effort readiness probe behind <c>/readyz</c> (operations.md section Observability):
/// pings vLLM and SearXNG via their named <see cref="IHttpClientFactory"/> clients (so
/// base URLs come from the same bound options the real adapters use) with a short
/// timeout. A dependency is "reachable" if the GET returns any HTTP response - we are
/// checking the socket/route, not asserting a specific status. Failures are swallowed
/// into <c>false</c>; readiness never throws.
/// </summary>
public static class ReadinessCheck
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Probe every upstream and return a name -> reachable map. The keys are the named
    /// HttpClient ids (<c>vllm</c>, <c>searxng</c>).
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, bool>> RunAsync(
        IHttpClientFactory httpFactory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);

        // The per-provider chat clients carry no base address (the SDK sets each endpoint),
        // so probe the embeddings client instead - it carries the Gert:Embeddings base URL,
        // the same OpenAI-compatible/vLLM upstream that serves chat in the reference deployment.
        var vllm = ProbeAsync(httpFactory, OpenAIEmbeddingClient.HttpClientName, cancellationToken);
        var searxng = ProbeAsync(httpFactory, SearXngWebSearch.HttpClientName, cancellationToken);

        await Task.WhenAll(vllm, searxng);

        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [OpenAIEmbeddingClient.HttpClientName] = vllm.Result,
            [SearXngWebSearch.HttpClientName] = searxng.Result,
        };
    }

    private static async Task<bool> ProbeAsync(
        IHttpClientFactory httpFactory, string clientName, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpFactory.CreateClient(clientName);
            if (client.BaseAddress is null)
            {
                return false;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, client.BaseAddress);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            // Any HTTP response means the upstream is routable/listening.
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
