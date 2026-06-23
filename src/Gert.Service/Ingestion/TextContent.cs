using System.Text;

namespace Gert.Service.Ingestion;

/// <summary>
/// The "is this UTF-8 text?" gate (chat-and-tools.md section ingestion). Gert accepts any upload;
/// type safety is decided here from the bytes, not from a filename. Used by the plain-text
/// extractor (ingestion) and the read_document resource (full-text read) so both judge text-ness
/// identically: a NUL byte, or a decode dominated by U+FFFD replacement chars, means the bytes are
/// not text.
/// </summary>
public static class TextContent
{
    /// <summary>
    /// Above this fraction of U+FFFD replacement chars, treat the decode as mis-decoded binary
    /// rather than text (a real text file has essentially none).
    /// </summary>
    public const double MaxReplacementFraction = 0.01;

    /// <summary>
    /// Try to decode <paramref name="bytes"/> as UTF-8 text (honouring a BOM). Returns false -
    /// with <paramref name="text"/> empty - when the bytes are not text (a NUL byte, or too many
    /// replacement chars).
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out string text)
    {
        text = string.Empty;

        // A NUL byte is the cheapest, most reliable "this is not text" signal.
        if (bytes.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        var start = HasUtf8Bom(bytes) ? 3 : 0;
        var decoded = Encoding.UTF8.GetString(bytes[start..]);

        if (ReplacementFraction(decoded) > MaxReplacementFraction)
        {
            return false;
        }

        text = decoded;
        return true;
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static double ReplacementFraction(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var replacements = 0;
        foreach (var c in text)
        {
            if (c == '�')
            {
                replacements++;
            }
        }

        return (double)replacements / text.Length;
    }
}
