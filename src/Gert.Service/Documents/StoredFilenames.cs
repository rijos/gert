using System.Text;

namespace Gert.Service.Documents;

/// <summary>
/// The one codec for the <c>documents.filename</c> column (storage-and-data.md
/// section rag.db): the original upload filename - or a memory entry's title - is stored
/// as base64(UTF-8) <b>display metadata</b>, never a storage path. Encode on write
/// (<see cref="DocumentService"/>, <c>MemoryService</c>); decode for display
/// (API responses, console output, the delete-key extension), falling back to the
/// stored value when a row somehow is not valid base64 - one odd row must never
/// make a list response 500 or a delete throw.
/// </summary>
public static class StoredFilenames
{
    /// <summary>Base64 of the UTF-8 original filename - the stored form.</summary>
    public static string Encode(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(filename));
    }

    /// <summary>
    /// Decode a stored base64 value back to the original UTF-8 name. Falls back to
    /// the raw value if it does not decode (defensive; never throws on a bad row).
    /// </summary>
    public static string Decode(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

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
