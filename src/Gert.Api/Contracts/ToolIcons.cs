namespace Gert.Api.Contracts;

/// <summary>
/// The closed icon vocabulary the client ships - EXACTLY the keys exported by the SPA's
/// <c>icons</c> map (wwwroot/icons/icons.ts), which is the source of truth. A tool's
/// <see cref="Gert.Tools.ITool.Icon"/> names a key here; the catalog
/// (<see cref="Gert.Api.Controllers.ToolsController"/>) degrades any unrecognised key (e.g. a
/// future MCP tool's) to <see cref="Fallback"/>, so the wire only ever carries a glyph the
/// client can render - the SPA never has to guess. A drift test keeps this set in lockstep
/// with icons.ts; a meta-test asserts every built-in tool's icon is a member.
/// </summary>
public static class ToolIcons
{
    /// <summary>The neutral glyph emitted for an unknown icon key. A real key in <see cref="Keys"/> and the default of <see cref="Gert.Tools.ITool.Icon"/>.</summary>
    public const string Fallback = "gear";

    /// <summary>The icon keys icons.ts exports, in its declaration order.</summary>
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
    {
        "plus", "close", "search", "file", "trash", "lock", "shield", "gear", "sidebar",
        "panel", "edit", "globe", "moon", "sun", "chevron", "send", "stop", "attach", "book",
        "upload", "download", "user", "copy", "check", "external", "clock", "checklist",
        "retry", "sparkle", "websearch",
    };
}
