namespace Gert.Console.Tui.State;

/// <summary>One <c>[n]</c> footnote under an assistant message.</summary>
public sealed record CitationNote(int Ordinal, string Label, string? Locator);
