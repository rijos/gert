namespace Gert.Service.Tools;

/// <summary>
/// One tool call from the model — the active project and the raw JSON arguments
/// (e.g. <c>{"query":"…","k":8}</c>).
/// </summary>
public sealed record ToolInvocation
{
    public required string Pid { get; init; }

    /// <summary>Raw tool arguments as a JSON string.</summary>
    public required string ArgumentsJson { get; init; }
}
