namespace Gert.Tools.Results;

/// <summary>
/// The model-facing payload of the web-fetch tool: the (clipped, plain-text) body
/// plus the provenance flags. Serialized snake_case - <c>extracted</c>,
/// <c>truncated</c>, <c>chars</c> - so the model knows what it is reading.
/// </summary>
public sealed record WebFetchResultPayload
{
    public required string Url { get; init; }

    public required string Content { get; init; }

    /// <summary>True if an HTML body was reduced to plain text before clipping.</summary>
    public required bool Extracted { get; init; }

    /// <summary>True if the content was clipped to the cap.</summary>
    public required bool Truncated { get; init; }

    /// <summary>The returned content length (after extraction + clip).</summary>
    public required int Chars { get; init; }
}
