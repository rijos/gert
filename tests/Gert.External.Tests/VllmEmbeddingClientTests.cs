using System.Linq;
using FluentAssertions;
using Gert.External.Vllm;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// Unit tests for the embeddings request/response shaping: the request carries the model
/// + input batch, and the response parser reads 1024-dim vectors, orders by index, and
/// asserts count + dimension.
/// </summary>
public sealed class VllmEmbeddingClientTests
{
    [Fact]
    public void BuildRequest_CarriesModelAndInputs()
    {
        var body = VllmEmbeddingClient.BuildRequest(["a", "b"], "bge-m3");

        body["model"]!.GetValue<string>().Should().Be("bge-m3");
        var input = body["input"]!.AsArray();
        input.Should().HaveCount(2);
        input[0]!.GetValue<string>().Should().Be("a");
        body["encoding_format"]!.GetValue<string>().Should().Be("float");
    }

    [Fact]
    public void ParseResponse_Reads1024DimVectors_OrderedByIndex()
    {
        var v0 = Enumerable.Repeat("0.5", 1024);
        var v1 = Enumerable.Repeat("0.25", 1024);
        var json = $$"""
        {
          "data": [
            { "index": 1, "embedding": [{{string.Join(",", v1)}}] },
            { "index": 0, "embedding": [{{string.Join(",", v0)}}] }
          ]
        }
        """;

        var vectors = VllmEmbeddingClient.ParseResponse(json, expectedCount: 2, expectedDimensions: 1024);

        vectors.Should().HaveCount(2);
        vectors[0].Should().HaveCount(1024);
        vectors[0][0].Should().BeApproximately(0.5f, 1e-6f); // index 0 first
        vectors[1][0].Should().BeApproximately(0.25f, 1e-6f);
    }

    [Fact]
    public void ParseResponse_WrongDimension_Throws()
    {
        var json = """{ "data": [ { "index": 0, "embedding": [0.1, 0.2, 0.3] } ] }""";
        var act = () => VllmEmbeddingClient.ParseResponse(json, expectedCount: 1, expectedDimensions: 1024);
        act.Should().Throw<InvalidOperationException>().WithMessage("*dimension*");
    }

    [Fact]
    public void ParseResponse_WrongCount_Throws()
    {
        var emb = string.Join(",", Enumerable.Repeat("0.1", 1024));
        var json = $$"""{ "data": [ { "index": 0, "embedding": [{{emb}}] } ] }""";
        var act = () => VllmEmbeddingClient.ParseResponse(json, expectedCount: 2, expectedDimensions: 1024);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ParseResponse_MissingData_Throws()
    {
        var act = () => VllmEmbeddingClient.ParseResponse("""{"object":"list"}""", 1, 1024);
        act.Should().Throw<InvalidOperationException>().WithMessage("*data*");
    }
}
