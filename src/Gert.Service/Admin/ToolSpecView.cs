namespace Gert.Service.Admin;

/// <summary>
/// One registered tool's spec, exactly as it would be advertised to the model
/// (chat-and-tools.md section chat orchestration). <see cref="ParametersSchema"/> is
/// the raw JSON-schema string the request builder embeds.
/// </summary>
public sealed record ToolSpecView
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string ParametersSchema { get; init; }
}
