using Gert.Tools.Schema;

namespace Gert.Tools.Args;

/// <summary>
/// Arguments for the document read tool (<c>read_document</c>): the <see cref="Doc"/> to read
/// (title or id) and an optional character window (<see cref="Offset"/> + <see cref="MaxChars"/>)
/// for paging through a large file. An empty <see cref="Doc"/> lists the available documents.
/// </summary>
public sealed record ReadDocumentArgs
{
    /// <summary>The document title (or id) to read; empty lists what is available.</summary>
    [ToolParameterDescription("Title (or id) of the document to read; leave empty to list available documents.")]
    public string Doc { get; init; } = string.Empty;

    /// <summary>Character offset to start from (default 0); use with the previous result's next offset to page.</summary>
    [ToolParameterDescription("Character offset to start reading from (default 0).")]
    public int? Offset { get; init; }

    /// <summary>Maximum characters to return in this read; the tool caps it.</summary>
    [ToolParameterDescription("Maximum characters to return (the tool caps very large reads).")]
    public int? MaxChars { get; init; }
}
