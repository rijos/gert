using Gert.Tools;

namespace Gert.Api.Contracts;

/// <summary>
/// The wire shape of one entitled tool in the <c>GET /api/tools</c> catalog
/// (rest-api.md section tools) - the source for the composer's tools popup. The popup
/// renders PURELY from this descriptor (title, icon, group, source) - no per-tool
/// knowledge lives in the SPA - so a future MCP source becomes another section for free.
/// Projected from a registered <see cref="ITool"/>; <see cref="ToolType"/> serializes as the
/// snake_case string enum (<c>"standard"</c> / <c>"modal"</c>) under <c>GertJsonOptions</c>.
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

    /// <summary>Display title for the menu row. Wire: <c>title</c>.</summary>
    public required string Title { get; init; }

    /// <summary>Icon key into the client's curated vocabulary (<see cref="ToolIcons"/>); guaranteed renderable. Wire: <c>icon</c>.</summary>
    public required string Icon { get; init; }

    /// <summary>The menu grouping the row sorts under (e.g. <c>standard</c> / <c>docs</c> / <c>canvas</c>). Wire: <c>group</c>.</summary>
    public required string Group { get; init; }

    /// <summary>The catalog the tool comes from (<c>builtin</c> today; an MCP server later) - the menu sections on it. Wire: <c>source</c>.</summary>
    public required string Source { get; init; }

    /// <summary>Whether the tool needs a human in the loop (<c>ask_user</c>). Wire: <c>requires_human</c>.</summary>
    public required bool RequiresHuman { get; init; }
}
