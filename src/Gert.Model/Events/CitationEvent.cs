namespace Gert.Model.Events;

/// <summary>
/// <c>citation</c> - the <c>[n]</c> marker + footnote (rest-api.md SSE table).
/// </summary>
public sealed record CitationEvent : ChatEvent
{
    public required int Ordinal { get; init; }

    public required string Label { get; init; }

    public string? DocId { get; init; }

    /// <summary>Locator within the source - a URL for web citations, "p.4"-style for documents.</summary>
    public string? Locator { get; init; }

    public override ChatEventType Type => ChatEventType.Citation;
}
