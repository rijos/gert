namespace Gert.Console.Tui.State;

/// <summary>
/// The visual class of one transcript line — the TUI's style vocabulary, the
/// analog of the SPA's message CSS classes. The view maps each kind to a
/// terminal attribute; the model never touches colors.
/// </summary>
public enum LineKind
{
    /// <summary>Blank separator line.</summary>
    Blank,

    /// <summary>"You" message header.</summary>
    UserHeader,

    /// <summary>"Gert" message header.</summary>
    AssistantHeader,

    /// <summary>Plain body text.</summary>
    Body,

    /// <summary>Markdown heading line.</summary>
    Heading,

    /// <summary>Fenced code-block line (monospace box on the web).</summary>
    Code,

    /// <summary>Collapsible thinking region header (▸/▾ Thinking).</summary>
    ThinkingHeader,

    /// <summary>Thinking body line (dim on the web).</summary>
    Thinking,

    /// <summary>Collapsible tool-card header (status icon, kind, latency).</summary>
    ToolHeader,

    /// <summary>Tool-card body line (hit row, stdout, todo).</summary>
    ToolBody,

    /// <summary>[n] citation footnote.</summary>
    Citation,

    /// <summary>Generation meta ("312 tok · 41 tok/s") and similar dim chrome.</summary>
    Meta,

    /// <summary>Error text.</summary>
    Error,
}
