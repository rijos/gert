namespace Gert.Service.Storage;

/// <summary>
/// One stored object's listing metadata - scope-relative <see cref="Key"/>, byte
/// <see cref="Size"/>, and <see cref="LastModified"/> instant. Maps 1:1 to what
/// every object backend returns from a listing (local <c>FileInfo</c>, S3
/// <c>ListObjectsV2</c>, Azure Blob listing).
/// </summary>
/// <param name="Key">Scope-relative key, <c>/</c>-separated.</param>
/// <param name="Size">Object size in bytes.</param>
/// <param name="LastModified">UTC last-modified instant.</param>
public sealed record ObjectEntry(string Key, long Size, DateTimeOffset LastModified);
