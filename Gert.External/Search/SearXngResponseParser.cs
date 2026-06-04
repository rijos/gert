using System.Text.Json;
using Gert.Service.External;

namespace Gert.External.Search;

/// <summary>
/// Pure parser for the SearXNG JSON search API response (<c>format=json</c>). Reads the
/// <c>results[]</c> array into <see cref="WebSearchResult"/>s, capped to
/// <paramref name="maxResults"/>. Network-free and so unit-testable on a canned body.
/// </summary>
public static class SearXngResponseParser
{
    /// <summary>Parse a SearXNG JSON body into capped results.</summary>
    public static IReadOnlyList<WebSearchResult> Parse(string json, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (maxResults <= 0)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<WebSearchResult>();
        foreach (var item in results.EnumerateArray())
        {
            if (list.Count >= maxResults)
            {
                break;
            }

            var url = GetString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            list.Add(new WebSearchResult
            {
                Title = GetString(item, "title") ?? url,
                Url = url,
                Snippet = GetString(item, "content"),
            });
        }

        return list;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
