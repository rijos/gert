using Gert.Service.Validation;

namespace Gert.Service.Storage;

/// <summary>
/// A read-only pass-through stream that tallies the bytes read from an inner
/// stream. Used by upload to record the true size of a streamed blob (when the host
/// did not supply <c>SizeBytes</c> up front) without buffering it - the bytes flow
/// straight into <see cref="IObjectStore.PutAsync"/> while being counted.
///
/// <para>
/// With a <c>limit</c>, the stream also <b>enforces</b> a byte cap mid-stream
/// (testing.md section 5: max upload size): the moment <see cref="BytesRead"/> exceeds it,
/// reading throws a <see cref="ValidationException"/> carrying the same
/// <c>upload.too_large</c> error code the <c>DocumentUploadValidator</c> produces,
/// so the host's ValidationExceptionHandler surfaces the identical branded 400
/// whether the size was known up front or only discovered while streaming.
/// </para>
/// </summary>
public sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly long? _limit;
    private readonly bool _leaveOpen;

    /// <summary>
    /// Wrap <paramref name="inner"/>, counting bytes read. With a non-null
    /// <paramref name="limit"/>, a read that pushes <see cref="BytesRead"/> past it
    /// throws (fail-closed, before the surplus bytes can be consumed downstream).
    /// Disposes the inner stream unless <paramref name="leaveOpen"/>.
    /// </summary>
    public CountingStream(Stream inner, long? limit = null, bool leaveOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _limit = limit;
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
        return Count(read);
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        return Count(read);
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Count(read);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <summary>
    /// Tally <paramref name="read"/> bytes and enforce the optional cap. The error
    /// mirrors <c>DocumentUploadValidator</c>'s size rule (same property, message
    /// shape and <c>upload.too_large</c> code) so both paths produce the same 400.
    /// </summary>
    private int Count(int read)
    {
        BytesRead += read;
        if (_limit is { } limit && BytesRead > limit)
        {
            throw new ValidationException(ValidationResult.Failure(
            [
                new ValidationError
                {
                    Property = "SizeBytes",
                    Message = $"Upload exceeds the {limit} byte limit.",
                    Code = "upload.too_large",
                },
            ]));
        }

        return read;
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
