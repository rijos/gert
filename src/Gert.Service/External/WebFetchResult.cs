namespace Gert.Service.External;

/// <summary>
/// One fetch outcome — either the (byte-capped) body text or a readable error
/// ("URL blocked by fetch policy", "fetch failed (404)"). The error is a tool
/// error the model reacts to, never a turn fault.
/// </summary>
public sealed record WebFetchResult
{
    public required bool Success { get; init; }

    /// <summary>The raw decoded body when <see cref="Success"/> (no text extraction — parity with web search's summarize clip).</summary>
    public string? Content { get; init; }

    /// <summary>Why the fetch was refused or failed, when not <see cref="Success"/>.</summary>
    public string? Error { get; init; }
}
