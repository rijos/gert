namespace Gert.Service.Storage;

/// <summary>
/// A read-only pass-through stream that tallies the bytes read from an inner
/// stream. Used by upload to record the true size of a streamed blob (when the host
/// did not supply <c>SizeBytes</c> up front) without buffering it — the bytes flow
/// straight into <see cref="IObjectStore.PutAsync"/> while being counted.
/// </summary>
public sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;

    /// <summary>Wrap <paramref name="inner"/>, counting bytes read. Disposes it unless <paramref name="leaveOpen"/>.</summary>
    public CountingStream(Stream inner, bool leaveOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _leaveOpen = leaveOpen;
    }

    /// <summary>Total bytes read through this stream so far.</summary>
    public long BytesRead { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        BytesRead += read;
        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        BytesRead += read;
        return read;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Flush()
    {
        // Read-only — nothing to flush.
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
