using Gert.Service.External;

namespace Gert.Testing.Fakes;

/// <summary>
/// SearXNG double (testing.md §4.1, A.4). Returns the canned result set for a
/// query from <c>fixtures.json</c>, capped to <c>maxResults</c>. At least one
/// fixture is adversarial by design — its result url is a link-local metadata
/// address — so the real adapter's SSRF guard (security F5) can be proven on the
/// E2E tier. The fake itself returns the row verbatim; refusing the fetch is the
/// adapter's job, not the search double's.
/// </summary>
public sealed class FakeWebSearch : IWebSearch
{
    private readonly Fixtures _fixtures;

    /// <summary>Build over the canonical shared fixtures.</summary>
    public FakeWebSearch()
        : this(Fixtures.Load())
    {
    }

    /// <summary>Build over explicit fixtures (tests may inject their own).</summary>
    public FakeWebSearch(Fixtures fixtures)
    {
        _fixtures = fixtures ?? throw new ArgumentNullException(nameof(fixtures));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var key = query.Trim();
        if (!_fixtures.Search.TryGetValue(key, out var fixture))
        {
            // Case-insensitive fallback so callers don't have to match casing exactly.
            var match = _fixtures.Search.Keys
                .FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                fixture = _fixtures.Search[match];
            }
        }

        IReadOnlyList<WebSearchResult> results = fixture is null
            ? []
            : fixture.Results
                .Take(maxResults < 0 ? 0 : maxResults)
                .Select(r => new WebSearchResult
                {
                    Title = r.Title,
                    Url = r.Url,
                    Snippet = r.Content,
                })
                .ToList();

        return Task.FromResult(results);
    }
}
