namespace Gert.Model.Chat;

/// <summary>A model-facing tool advertised in a completion request.</summary>
public sealed record ChatToolSpec
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>JSON-schema of the tool's parameters (as a JSON string).</summary>
    public required string ParametersSchema { get; init; }
}
