using System.Text.Json.Serialization;

namespace Gert.Testing.Fakes;

/// <summary>One SearXNG result row.</summary>
public sealed class SearchResultFixture
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}
