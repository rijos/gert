using Gert.Tools;

namespace Gert.Api.Contracts;

/// <summary>
/// The wire shape of one entitled tool in the <c>GET /api/tools</c> catalog
/// (rest-api.md section tools) - the source for the composer's tools popup.
/// Projected from a registered <see cref="ITool"/>, so the popup's labels and
/// grouping ride the server's own tool identity instead of a hardcoded SPA list.
/// <see cref="ToolType"/> serializes as the snake_case string enum
/// (<c>"standard"</c> / <c>"modal"</c>) under <c>GertJsonOptions</c>.
/// </summary>
public sealed record ToolInfo
{
    /// <summary>Capability id == the <c>gert_tools</c> entitlement name (e.g. <c>rag</c>). Wire: <c>id</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Model-facing function name (e.g. <c>search_documents</c>). Wire: <c>name</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The model-readable description advertised for the tool. Wire: <c>description</c>.</summary>
    public required string Description { get; init; }

    /// <summary>The tool's execution flow (<c>standard</c> / <c>modal</c>). Wire: <c>tool_type</c>.</summary>
    public required ToolType ToolType { get; init; }
}
