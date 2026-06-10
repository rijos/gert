using Gert.Model;
using Gert.Model.Rag;
using Gert.Service.Documents;

namespace Gert.Api.Contracts;

/// <summary>
/// The wire shape of a RAG document (rest-api.md § documents). It mirrors the
/// service <see cref="Document"/> but returns the <b>decoded original filename</b>:
/// the service stores <c>documents.filename</c> as base64 display metadata
/// (never a path), and the SPA wants the human-readable name (which it renders
/// safely as a text node). Decoding here keeps that one transport concern out of
/// the service.
/// </summary>
public sealed record DocumentResponse
{
    public required string Id { get; init; }

    /// <summary>The decoded original upload filename (base64 on disk, decoded here). Wire: <c>name</c>.</summary>
    public required string Name { get; init; }

    public required string Mime { get; init; }

    /// <summary>Byte size. Wire: <c>size</c>.</summary>
    public required long Size { get; init; }

    public required DocumentStatus Status { get; init; }

    public int ChunkCount { get; init; }

    public string? Error { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Project a service <see cref="Document"/>, decoding the base64 filename.</summary>
    public static DocumentResponse From(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new DocumentResponse
        {
            Id = document.Id,
            Name = StoredFilenames.Decode(document.Filename),
            Mime = document.Mime,
            Size = document.SizeBytes,
            Status = document.Status,
            ChunkCount = document.ChunkCount,
            Error = document.Error,
            CreatedAt = document.CreatedAt,
        };
    }
}
