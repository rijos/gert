namespace Gert.Model.Events;

/// <summary>
/// <c>citation</c> — the <c>[n]</c> marker + footnote (rest-api.md SSE table).
/// </summary>
public sealed record CitationEvent : ChatEvent
{
    public required int Ordinal { get; init; }

    public required string Label { get; init; }

    public string? DocId { get; init; }

    public override ChatEventType Type => ChatEventType.Citation;
}
