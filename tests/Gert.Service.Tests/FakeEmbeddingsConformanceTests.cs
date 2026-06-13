using System.Text.Json;
using Gert.Testing;
using Gert.Testing.Fakes;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// The anti-drift conformance check (testing.md Appendix A.2 / A.5): the .NET
/// <see cref="FakeEmbeddings"/> must reproduce the committed golden vectors to
/// <b>float32 equality</b>. The Python mock asserts the same golden, so if either
/// implementation drifts, both go red.
/// </summary>
/// <remarks>
/// The golden file is committed at
/// <c>tests/shared/embeddings_golden.json</c>. Expected schema:
/// <code>
/// {
///   "dimensions": 1024,
///   "vectors": {
///     "&lt;text&gt;": [ 0.0123, -0.0456, ... ]   // 1024 float32 values
///   }
/// }
/// </code>
/// </remarks>
public sealed class FakeEmbeddingsConformanceTests
{
    public static IEnumerable<object[]> GoldenTexts() =>
        Golden.Value.Vectors.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(GoldenTexts))]
    public void Embed_matches_golden_to_float32(string text)
    {
        var expected = Golden.Value.Vectors[text];
        var actual = FakeEmbeddings.Embed(text);

        Assert.Equal(FakeEmbeddings.Dimensions, actual.Length);
        Assert.Equal(expected.Length, actual.Length);

        for (var i = 0; i < expected.Length; i++)
        {
            // Exact float32 equality - the conformance contract is bit-for-bit.
            Assert.Equal(expected[i], actual[i]);
        }
    }

    [Fact]
    public void Golden_file_is_present_and_non_empty()
    {
        Assert.True(
            File.Exists(SharedPaths.EmbeddingsGoldenJson),
            $"Missing golden file at {SharedPaths.EmbeddingsGoldenJson}. " +
            "Regenerate it by running the A.2 algorithm (see testing.md).");
        Assert.NotEmpty(Golden.Value.Vectors);
    }

    private static readonly Lazy<GoldenFile> Golden = new(Load);

    private static GoldenFile Load()
    {
        var json = File.ReadAllText(SharedPaths.EmbeddingsGoldenJson);
        return JsonSerializer.Deserialize<GoldenFile>(
                   json,
                   new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidOperationException("embeddings_golden.json deserialized to null.");
    }

    private sealed class GoldenFile
    {
        public int Dimensions { get; init; }

        public IReadOnlyDictionary<string, float[]> Vectors { get; init; } =
            new Dictionary<string, float[]>();
    }
}
