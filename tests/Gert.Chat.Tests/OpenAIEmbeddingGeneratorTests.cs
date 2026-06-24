using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Gert.Chat.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gert.Chat.Tests;

/// <summary>
/// Drives the real <see cref="OpenAIEmbeddingGenerator"/> (over the M.E.AI OpenAI adapter) through a
/// stubbed <see cref="HttpMessageHandler"/>: the request carries the model + input batch, the adapter
/// reads the vectors ordered by index, and the wrapper asserts count + dimension. No live server.
/// </summary>
public sealed class OpenAIEmbeddingGeneratorTests
{
    private static (OpenAIEmbeddingGenerator Generator, StubHttpMessageHandler Handler) NewGenerator(
        string json, int dimensions, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

        var http = new HttpClient(handler);
        var parameters = new EmbeddingsParameters
        {
            BaseUrl = "http://openai.test:8000",
            Model = "bge-m3",
            Dimensions = dimensions,
        };
        return (new OpenAIEmbeddingGenerator(http, parameters, NullLogger<OpenAIEmbeddingGenerator>.Instance), handler);
    }

    private static string ResponseJson(params (int Index, float[] Vector)[] data) =>
        $$"""
        {
          "object": "list",
          "model": "bge-m3",
          "data": [{{string.Join(",", data.Select(d =>
              $$"""{ "object": "embedding", "index": {{d.Index}}, "embedding": [{{string.Join(",", d.Vector)}}] }"""))}}],
          "usage": { "prompt_tokens": 1, "total_tokens": 1 }
        }
        """;

    [Fact]
    public async Task GenerateAsync_SendsModelAndInputBatch()
    {
        var (generator, handler) = NewGenerator(
            ResponseJson((0, [0.1f, 0.2f]), (1, [0.3f, 0.4f])), dimensions: 2);

        await generator.GenerateAsync(["a", "b"]);

        handler.LastRequestUri!.AbsolutePath.Should().Be("/v1/embeddings");
        var body = JsonNode.Parse(handler.LastRequestBody!)!;
        body["model"]!.GetValue<string>().Should().Be("bge-m3");
        var input = body["input"]!.AsArray();
        input.Should().HaveCount(2);
        input[0]!.GetValue<string>().Should().Be("a");
        input[1]!.GetValue<string>().Should().Be("b");
    }

    [Fact]
    public async Task GenerateAsync_ReadsVectors_OrderedByIndex()
    {
        // The server may return entries out of order - index decides.
        var (generator, _) = NewGenerator(
            ResponseJson((1, [0.25f, 0.25f]), (0, [0.5f, 0.5f])), dimensions: 2);

        var vectors = await generator.GenerateAsync(["a", "b"]);

        vectors.Should().HaveCount(2);
        vectors[0].Vector.Span[0].Should().BeApproximately(0.5f, 1e-6f);
        vectors[1].Vector.Span[0].Should().BeApproximately(0.25f, 1e-6f);
    }

    [Fact]
    public async Task GenerateAsync_EmptyBatch_ShortCircuitsWithoutARequest()
    {
        var (generator, handler) = NewGenerator(ResponseJson(), dimensions: 2);

        var vectors = await generator.GenerateAsync([]);

        vectors.Should().BeEmpty();
        handler.LastRequestUri.Should().BeNull();
    }

    [Fact]
    public async Task GenerateAsync_WrongDimension_Throws()
    {
        var (generator, _) = NewGenerator(ResponseJson((0, [0.1f, 0.2f, 0.3f])), dimensions: 1024);

        var act = () => generator.GenerateAsync(["a"]);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("dimension");
    }

    [Fact]
    public async Task GenerateAsync_WrongCount_Throws()
    {
        var (generator, _) = NewGenerator(ResponseJson((0, [0.1f, 0.2f])), dimensions: 2);

        var act = () => generator.GenerateAsync(["a", "b"]);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GenerateAsync_UpstreamError_SurfacesAsHttpRequestException()
    {
        var (generator, _) = NewGenerator(
            """{"error":{"message":"boom"}}""", dimensions: 2, HttpStatusCode.InternalServerError);

        var act = () => generator.GenerateAsync(["a"]);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
