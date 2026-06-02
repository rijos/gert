using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gert.Testing.Fakes;

/// <summary>
/// Loads and exposes <c>tests/shared/fixtures.json</c> — the single source of
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

    /// <summary>Parse fixtures from a JSON string.</summary>
    public static Fixtures Parse(string json) =>
        JsonSerializer.Deserialize<Fixtures>(json, Options)
        ?? throw new InvalidOperationException("fixtures.json deserialized to null.");

    /// <summary>Load fixtures from the canonical <c>tests/shared/fixtures.json</c>.</summary>
    public static Fixtures Load() => Parse(File.ReadAllText(SharedPaths.FixturesJson));
}

/// <summary>One canned chat completion (testing.md A.3).</summary>
public sealed class CompletionFixture
{
    /// <summary><c>exact</c> | <c>contains</c> — how <see cref="When"/> matches the last user message.</summary>
    [JsonPropertyName("match")]
    public string Match { get; init; } = "exact";

    /// <summary>The trimmed last user message this fixture responds to.</summary>
    [JsonPropertyName("when")]
    public string When { get; init; } = string.Empty;

    /// <summary>Content deltas, streamed one chunk per element.</summary>
    [JsonPropertyName("deltas")]
    public IReadOnlyList<string> Deltas { get; init; } = [];

    /// <summary>OpenAI finish reason — <c>stop</c>, or <c>tool_calls</c> when <see cref="ToolCall"/> is set.</summary>
    [JsonPropertyName("finish")]
    public string Finish { get; init; } = "stop";

    /// <summary>Optional token-usage block.</summary>
    [JsonPropertyName("usage")]
    public UsageFixture? Usage { get; init; }

    /// <summary>If set, this fixture emits a tool call first (the tool-loop path).</summary>
    [JsonPropertyName("tool_call")]
    public ToolCallFixture? ToolCall { get; init; }

    /// <summary>Played on the follow-up call once the messages carry the tool result.</summary>
    [JsonPropertyName("after_tool")]
    public AfterToolFixture? AfterTool { get; init; }
}

/// <summary>A scripted model tool call (OpenAI shape: arguments is a JSON string).</summary>
public sealed class ToolCallFixture
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = "{}";
}

/// <summary>The follow-up completion played after a tool result returns.</summary>
public sealed class AfterToolFixture
{
    [JsonPropertyName("deltas")]
    public IReadOnlyList<string> Deltas { get; init; } = [];

    [JsonPropertyName("finish")]
    public string Finish { get; init; } = "stop";

    [JsonPropertyName("usage")]
    public UsageFixture? Usage { get; init; }
}

/// <summary>Token-usage counts.</summary>
public sealed class UsageFixture
{
    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; init; }
}

/// <summary>Canned web-search results for one query (A.4 — SearXNG <c>results</c> shape).</summary>
public sealed class SearchFixture
{
    [JsonPropertyName("results")]
    public IReadOnlyList<SearchResultFixture> Results { get; init; } = [];
}

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
