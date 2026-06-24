using Gert.Tools;
using Gert.Tools.Args;
using Gert.Tools.Hosting;
using Gert.Tools.Results;
using Gert.Validation;

namespace Gert.Tools.Builtin;

/// <summary>
/// The document read tool. Model function <c>read_document</c>: return the <b>full</b> text of one
/// of this project's uploaded documents (read from the original file, not RAG snippets) so the
/// model can transform or reproduce an entire file - the use case <c>search_documents</c> cannot
/// serve. Large files are paged with <c>offset</c>; an empty <c>doc</c>, or one that does not
/// resolve, returns the list of available titles. Read-only; reads through the host's pre-scoped
/// <see cref="Gert.Tools.Resources.IDocumentResource"/>.
/// </summary>
public sealed class ReadDocumentTool(IValidationProvider validation)
    : ToolCall<ReadDocumentArgs, ReadDocumentResult>(validation)
{
    /// <summary>Default characters returned when the model omits <c>max_chars</c>.</summary>
    private const int DefaultMaxChars = 200_000;

    /// <summary>Hard cap per read (the model pages a larger file with <c>offset</c>).</summary>
    private const int MaxCharsCap = 200_000;

    /// <inheritdoc />
    public override string Id => "read_document";

    /// <inheritdoc />
    public override string Name => "read_document";

    /// <inheritdoc />
    public override string Title => "Read document";

    /// <inheritdoc />
    public override string Icon => "file";

    /// <inheritdoc />
    public override string Group => "docs";

    /// <inheritdoc />
    public override string Description =>
        "Read a project document's FULL text by title (paged with offset for large files). "
        + "Use this - not search_documents, which returns only excerpts - whenever a task needs the "
        + "whole file: reformat, translate, rewrite, or summarise it. Leave doc empty to list documents.";

    /// <inheritdoc />
    public override async Task<ToolCallResult<ReadDocumentResult>> CallAsync(
        ReadDocumentArgs args,
        ToolInvocation invocation,
        IToolHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(host);

        var docs = host.Resources.Documents;

        if (string.IsNullOrWhiteSpace(args.Doc))
        {
            return ToolCallResult<ReadDocumentResult>.Ok(
                await ListingAsync(docs, note: "Specify one of these in 'doc'.", cancellationToken)
                    .ConfigureAwait(false));
        }

        var offset = Math.Max(0, args.Offset ?? 0);
        var maxChars = Math.Clamp(args.MaxChars ?? DefaultMaxChars, 1, MaxCharsCap);

        var content = await docs.ReadAsync(args.Doc, offset, maxChars, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return ToolCallResult<ReadDocumentResult>.Ok(
                await ListingAsync(docs, note: $"No document matched '{args.Doc}'.", cancellationToken)
                    .ConfigureAwait(false));
        }

        if (!content.IsText)
        {
            return ToolCallResult<ReadDocumentResult>.Ok(new ReadDocumentResult
            {
                Doc = content.Title,
                Note = "This document is not text; use search_documents to retrieve passages from it.",
            });
        }

        return ToolCallResult<ReadDocumentResult>.Ok(new ReadDocumentResult
        {
            Doc = content.Title,
            Content = content.Content,
            TotalChars = content.TotalChars,
            Offset = content.Offset,
            HasMore = content.HasMore,
            NextOffset = content.Offset + content.Content.Length,
        });
    }

    private static async Task<ReadDocumentResult> ListingAsync(
        Gert.Tools.Resources.IDocumentResource docs, string note, CancellationToken cancellationToken)
    {
        var available = await docs.ListAsync(cancellationToken).ConfigureAwait(false);
        var titles = available.Select(d => d.Title).ToList();
        return new ReadDocumentResult
        {
            Note = titles.Count == 0 ? "This project has no documents." : note,
            Available = titles,
        };
    }
}
