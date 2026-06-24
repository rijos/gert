namespace Gert.Tools.Results;

/// <summary>
/// The model-facing payload of the web-search tool: the result rows the model
/// reads back. Serialized snake_case to <c>{ "results": [...] }</c>; the web
/// <see cref="Citation"/>s ride the side-channel.
/// </summary>
public sealed record WebSearchToolResult
{
    public required IReadOnlyList<WebSearchHit> Results { get; init; }
}
