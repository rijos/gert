using System.Text.Json.Serialization;

namespace Gert.Testing.Fakes;

/// <summary>
/// One canned fetch outcome from <c>fixtures.json</c>'s <c>fetch</c> section,
/// keyed by URL: either a body (<see cref="Content"/>) or a policy refusal
/// (<see cref="Blocked"/>) — the shape <see cref="FakeWebFetcher"/> replays.
/// </summary>
public sealed record FetchFixture
{
    /// <summary>The canned body for a successful fetch.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>True to replay the SSRF-blocked outcome (security F5).</summary>
    [JsonPropertyName("blocked")]
    public bool Blocked { get; init; }
}
