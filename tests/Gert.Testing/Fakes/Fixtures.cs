using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gert.Testing.Fakes;

/// <summary>
/// Loads and exposes <c>tests/shared/fixtures.json</c> - the single source of
/// truth both fake layers consume (testing.md Appendix A.5). The schema mirrors
/// A.3 (chat completions) and A.4 (web search). Unknown keys (e.g. the
/// <c>$comment</c> annotations in the file) are ignored.
/// </summary>
public sealed class Fixtures
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The <c>fallback</c> strategy when no completion matches (e.g. <c>echo</c>).</summary>
    [JsonPropertyName("fallback")]
    public string Fallback { get; init; } = "echo";

    /// <summary>Canned chat completions (A.3).</summary>
    [JsonPropertyName("completions")]
    public IReadOnlyList<CompletionFixture> Completions { get; init; } = [];

    /// <summary>Canned web-search results keyed by query (A.4).</summary>
    [JsonPropertyName("search")]
    public IReadOnlyDictionary<string, SearchFixture> Search { get; init; } =
        new Dictionary<string, SearchFixture>();

    /// <summary>Canned web-fetch outcomes keyed by URL (the web_fetch tool's fake).</summary>
    [JsonPropertyName("fetch")]
    public IReadOnlyDictionary<string, FetchFixture> Fetch { get; init; } =
        new Dictionary<string, FetchFixture>();

    public static Fixtures Parse(string json) =>
        JsonSerializer.Deserialize<Fixtures>(json, Options)
        ?? throw new InvalidOperationException("fixtures.json deserialized to null.");

    /// <summary>Load fixtures from the canonical <c>tests/shared/fixtures.json</c>.</summary>
    public static Fixtures Load() => Parse(File.ReadAllText(SharedPaths.FixturesJson));
}
