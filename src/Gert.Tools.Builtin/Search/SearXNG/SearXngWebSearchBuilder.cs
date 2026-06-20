using Gert.Tools;
using Gert.Tools.Fetch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Tools.Search.SearXNG;

/// <summary>
/// The <c>SearXNG</c> web-search plugin (<see cref="IWebSearchBuilder"/>): builds a
/// <see cref="SearXngWebSearch"/> over the named SearXNG <c>HttpClient</c> + the shared
/// <see cref="SearXngOptions"/> caps + the SSRF-guarded <see cref="SafeHttpFetcher"/>.
/// Registered keyed by its <see cref="Type"/> in <c>AddGertSearchSearXNG</c>; the generic
/// <see cref="WebSearchFactory"/> resolves it when <c>Gert:Tools:Search:Type</c> is
/// <c>SearXNG</c> - no central switch over Type.
/// </summary>
public sealed class SearXngWebSearchBuilder : IWebSearchBuilder
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<SearXngOptions> _options;
    private readonly SafeHttpFetcher _fetcher;
    private readonly ILoggerFactory _loggerFactory;

    public SearXngWebSearchBuilder(
        IHttpClientFactory httpFactory,
        IOptions<SearXngOptions> options,
        SafeHttpFetcher fetcher,
        ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc />
    public string Type => "SearXNG";

    /// <inheritdoc />
    public IWebSearch Build() => new SearXngWebSearch(
        _httpFactory.CreateClient(SearXngWebSearch.HttpClientName),
        _options,
        _fetcher,
        _loggerFactory.CreateLogger<SearXngWebSearch>());
}
