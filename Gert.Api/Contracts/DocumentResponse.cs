using System.Text;
using Gert.Model;
using Gert.Model.Rag;

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

    /// <summary>The decoded original upload filename (base64 on disk, decoded here).</summary>
    public required string Filename { get; init; }

    public required string Mime { get; init; }

    public required long SizeBytes { get; init; }

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
            Filename = DecodeFilename(document.Filename),
            Mime = document.Mime,
            SizeBytes = document.SizeBytes,
            Status = document.Status,
            ChunkCount = document.ChunkCount,
            Error = document.Error,
            CreatedAt = document.CreatedAt,
        };
    }

    /// <summary>
    /// Decode a base64 <c>documents.filename</c> to the original UTF-8 name. A row
    /// that somehow is not valid base64 falls back to the stored value rather than
    /// throwing — a list response must never 500 on one odd row.
    /// </summary>
    private static string DecodeFilename(string stored)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(stored));
        }
        catch (FormatException)
        {
            return stored;
        }
    }
}
