namespace Gert.Tools.Args;

/// <summary>
/// Arguments for the web-fetch tool (<c>web_fetch</c>): the absolute http(s)
/// <see cref="Url"/> plus an optional <see cref="MaxChars"/> cap (wire
/// <c>max_chars</c>). An over-the-ceiling cap is clamped by the tool, not
/// errored, so the validator only floors a supplied value at 1.
/// </summary>
public sealed record WebFetchArgs
{
    /// <summary>The absolute http(s) URL to fetch (required).</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Optional cap on the returned content; null defaults to 8000.</summary>
    public int? MaxChars { get; init; }
}
