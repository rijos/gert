using Gert.Model.Chat;

namespace Gert.Api.Contracts;

/// <summary>
/// A footnote marker on a <see cref="ThreadMessage"/> - the
/// <c>{ ordinal, label, doc_id, locator }</c> shape the SPA injects as a <c>[n]</c>
/// chip and lists in the sources card (the locator is the URL for web sources).
/// </summary>
public sealed record ThreadCitation
{
    public required int Ordinal { get; init; }

    public required string Label { get; init; }

    public string? DocId { get; init; }

    /// <summary>Locator within the source - a URL for web citations, "p.4"-style for documents.</summary>
    public string? Locator { get; init; }

    /// <summary>Project a persisted <see cref="Citation"/>.</summary>
    public static ThreadCitation From(Citation citation)
    {
        ArgumentNullException.ThrowIfNull(citation);

        return new ThreadCitation
        {
            Ordinal = citation.Ordinal,
            Label = citation.Label,
            DocId = citation.DocId,
            Locator = citation.Locator,
        };
    }
}
