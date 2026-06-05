using Gert.Model.Chat;

namespace Gert.Api.Contracts;

/// <summary>
/// A footnote marker on a <see cref="ThreadMessage"/> — the
/// <c>{ ordinal, label, doc_id }</c> shape the SPA injects as a <c>[n]</c> chip.
/// </summary>
public sealed record ThreadCitation
{
    public required int Ordinal { get; init; }

    public required string Label { get; init; }

    public string? DocId { get; init; }

    /// <summary>Project a persisted <see cref="Citation"/>.</summary>
    public static ThreadCitation From(Citation citation)
    {
        ArgumentNullException.ThrowIfNull(citation);

        return new ThreadCitation
        {
            Ordinal = citation.Ordinal,
            Label = citation.Label,
            DocId = citation.DocId,
        };
    }
}
