using System.Text.Json.Serialization;

namespace Gert.Testing.Fakes;

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
