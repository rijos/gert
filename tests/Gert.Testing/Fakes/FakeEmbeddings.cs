using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Gert.Service.External;

namespace Gert.Testing.Fakes;

/// <summary>
/// Deterministic <see cref="IEmbeddingClient"/> double (testing.md Appendix A.2).
/// Maps text -> a stable 1024-dim L2-unit vector, hash-seeded, so KNN/RRF order
/// is identical across runs and across the .NET fake and the Python mock. The
/// algorithm is specified to the byte; see <see cref="Embed"/>.
/// </summary>
public sealed class FakeEmbeddings : IEmbeddingClient
{
    /// <summary>The embedding dimension - bge-m3 = 1024 (see <see cref="IEmbeddingClient"/>).</summary>
    public const int Dimensions = 1024;

    /// <inheritdoc />
    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var vectors = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            vectors[i] = Embed(texts[i]);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    /// <summary>
    /// Embed a single text per the A.2 algorithm:
    /// <code>
    /// data = utf8(text)
    /// for i in 0..1023:
    ///     h    = SHA256( data ++ uint32_be(i) )   // index suffix big-endian
    ///     u    = uint32_be( h[0:4] )              // first 4 bytes big-endian
    ///     x[i] = (u / 4294967296.0) * 2.0 - 1.0   // double in [-1, 1)
    /// norm = sqrt( sum_i x[i]^2 )                 // L2 norm in double
    /// return [ float32( x[i] / norm ) for i ]     // cast to float32 only at the end
    /// </code>
    /// All arithmetic is IEEE-754 double; the cast to float32 happens once, at the
    /// very end. Big-endian throughout. If <c>norm</c> were zero (never in practice),
    /// the canonical basis vector e0 is returned.
    /// </summary>
    public static float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var data = Encoding.UTF8.GetBytes(text);

        // Buffer = data ++ 4-byte big-endian index. Reused per i; only the suffix changes.
        var buffer = new byte[data.Length + sizeof(uint)];
        Array.Copy(data, buffer, data.Length);

        var x = new double[Dimensions];
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];

        for (var i = 0; i < Dimensions; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(data.Length), (uint)i);
            SHA256.HashData(buffer, digest);
            uint u = BinaryPrimitives.ReadUInt32BigEndian(digest);
            x[i] = ((u / 4294967296.0) * 2.0) - 1.0;
        }

        var sumSquares = 0.0;
        for (var i = 0; i < Dimensions; i++)
        {
            sumSquares += x[i] * x[i];
        }

        var norm = Math.Sqrt(sumSquares);

        var result = new float[Dimensions];
        if (norm == 0.0)
        {
            result[0] = 1.0f;
            return result;
        }

        for (var i = 0; i < Dimensions; i++)
        {
            result[i] = (float)(x[i] / norm);
        }

        return result;
    }
}
