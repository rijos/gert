using Gert.Model;
using Gert.Model.Tools;
using Gert.Tools;
using Gert.Tools.Ports;

namespace Gert.Testing.Fakes;

/// <summary>
/// Web-fetch double (testing.md section 4.1). Replays the canned outcome for a URL
/// from <c>fixtures.json</c>'s <c>fetch</c> section: a body, or the
/// SSRF-blocked refusal (security F5) - needed because the REAL
/// <c>Gert.Tools</c> fetcher rightly blocks loopback, i.e. it would
/// refuse the in-process mock servers themselves. An unknown URL fails like a
/// dead host so a typo'd fixture shows up as a readable card error, not a hang.
/// </summary>
public sealed class FakeWebFetcher : IWebFetcher
{
    private readonly Fixtures _fixtures;

    public FakeWebFetcher()
        : this(Fixtures.Load())
    {
    }

    /// <summary>Build over explicit fixtures (tests may inject their own).</summary>
    public FakeWebFetcher(Fixtures fixtures)
    {
        _fixtures = fixtures ?? throw new ArgumentNullException(nameof(fixtures));
    }

    /// <inheritdoc />
    public Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        cancellationToken.ThrowIfCancellationRequested();

        var key = url.Trim();
        if (!_fixtures.Fetch.TryGetValue(key, out var fixture))
        {
            // Case-insensitive fallback so callers don't have to match casing exactly.
            var match = _fixtures.Fetch.Keys
                .FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                fixture = _fixtures.Fetch[match];
            }
        }

        var result = fixture switch
        {
            null => new WebFetchResult { Success = false, Error = $"fetch failed (no fixture for {key})" },
            { Blocked: true } => new WebFetchResult { Success = false, Error = "URL blocked by fetch policy" },
            _ => new WebFetchResult { Success = true, Content = fixture.Content ?? string.Empty },
        };

        return Task.FromResult(result);
    }
}
