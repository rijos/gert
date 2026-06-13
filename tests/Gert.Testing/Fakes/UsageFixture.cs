using System.Text.Json.Serialization;

namespace Gert.Testing.Fakes;

/// <summary>Token-usage counts.</summary>
public sealed class UsageFixture
{
    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; init; }
}
