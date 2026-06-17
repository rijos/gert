namespace Gert.Ingestion.Subprocess;

/// <summary>
/// Pure decompression-budget tracker for the DOCX (OOXML zip) path (security F7). A
/// crafted zip can declare a tiny compressed size but expand to gigabytes, or pack
/// millions of entries. The extractor feeds each entry's declared/decompressed size and
/// the running entry count through this guard; the first breach trips it so the caller
/// aborts before exhausting memory. Network- and IO-free, so it's unit-tested directly.
/// </summary>
public sealed class ZipBombGuard
{
    private readonly long _maxDecompressedBytes;
    private readonly int _maxEntries;
    private long _decompressedSoFar;
    private int _entriesSoFar;

    /// <summary>Create over the configured decompressed-size + entry-count caps.</summary>
    public ZipBombGuard(long maxDecompressedBytes, int maxEntries)
    {
        if (maxDecompressedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDecompressedBytes));
        }

        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        _maxDecompressedBytes = maxDecompressedBytes;
        _maxEntries = maxEntries;
    }

    /// <summary>True once a cap has been breached; the caller must stop extracting.</summary>
    public bool Tripped { get; private set; }

    /// <summary>The reason the guard tripped, or null if it hasn't.</summary>
    public string? TripReason { get; private set; }

    /// <summary>
    /// Account for one more entry of <paramref name="decompressedBytes"/>. Returns
    /// <c>false</c> (and sets <see cref="Tripped"/>) if either the entry-count or the
    /// cumulative-decompressed-size cap would be exceeded.
    /// </summary>
    public bool TryAccountEntry(long decompressedBytes)
    {
        if (Tripped)
        {
            return false;
        }

        if (decompressedBytes < 0)
        {
            Trip("Negative decompressed size reported.");
            return false;
        }

        _entriesSoFar++;
        if (_entriesSoFar > _maxEntries)
        {
            Trip($"Zip entry count exceeded cap of {_maxEntries}.");
            return false;
        }

        _decompressedSoFar += decompressedBytes;
        if (_decompressedSoFar > _maxDecompressedBytes)
        {
            Trip($"Decompressed size exceeded cap of {_maxDecompressedBytes} bytes.");
            return false;
        }

        return true;
    }

    private void Trip(string reason)
    {
        Tripped = true;
        TripReason = reason;
    }
}
