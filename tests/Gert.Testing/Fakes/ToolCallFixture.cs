using System.Text.Json.Serialization;

namespace Gert.Testing.Fakes;

/// <summary>A scripted model tool call (OpenAI shape: arguments is a JSON string).</summary>
public sealed class ToolCallFixture
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = "{}";
}
