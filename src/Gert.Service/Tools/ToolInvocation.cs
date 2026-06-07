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

    /// <summary>
    /// The conversation this call runs in. Optional so callers that don't need it
    /// (most tools) keep their construction unchanged; the artifact tools require
    /// it to scope/persist canvas artifacts and error without it.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>The assistant message producing this call (artifact provenance).</summary>
    public string? MessageId { get; init; }
}
