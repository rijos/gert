namespace Gert.Console.Tui.State;

/// <summary>
/// One renderable transcript line: unwrapped text + its <see cref="LineKind"/>
/// + the collapse region it belongs to. The flat line list is the headless
/// render model — tests assert it directly; <c>TranscriptView</c> only wraps
/// and colors it.
/// </summary>
public sealed record RenderLine
{
    public required string Text { get; init; }

    public required LineKind Kind { get; init; }

    /// <summary>
    /// Collapse-region id (e.g. <c>think:3</c>, <c>tool:call-7</c>) — set on the
    /// region's header AND body lines; Enter on the header toggles the region.
    /// </summary>
    public string? RegionId { get; init; }

    /// <summary>True on the ▸/▾ header line of a collapsible region.</summary>
    public bool IsRegionHeader { get; init; }
}
