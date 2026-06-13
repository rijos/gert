using System.Text.Json.Serialization;

namespace Gert.Testing.Fakes;

/// <summary>Canned web-search results for one query (A.4 - SearXNG <c>results</c> shape).</summary>
public sealed class SearchFixture
{
    [JsonPropertyName("results")]
    public IReadOnlyList<SearchResultFixture> Results { get; init; } = [];
}
