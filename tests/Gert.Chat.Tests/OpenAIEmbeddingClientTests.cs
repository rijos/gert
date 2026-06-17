using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Gert.Chat.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Chat.Tests;

/// <summary>
/// Drives the real <see cref="OpenAIEmbeddingClient"/> through a stubbed
/// <see cref="HttpMessageHandler"/>: the request carries the model + input batch, and
/// the response mapping reads the vectors, orders by index, and asserts count +
/// dimension. No live server.
/// </summary>
public sealed class OpenAIEmbeddingClientTests
{
    private static (OpenAIEmbeddingClient Client, StubHttpMessageHandler Handler) NewClient(
        string json, int dimensions, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

        var http = new HttpClient(handler);
        var options = Options.Create(new EmbeddingsOptions
        {
            Parameters = new EmbeddingsParameters
            {
                BaseUrl = "http://openai.test:8000",
                Model = "bge-m3",
                Dimensions = dimensions,
            },
        });
        return (new OpenAIEmbeddingClient(http, options, NullLogger<OpenAIEmbeddingClient>.Instance), handler);
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
    public async Task EmbedAsync_SendsModelAndInputBatch()
    {
        var (client, handler) = NewClient(
            ResponseJson((0, [0.1f, 0.2f]), (1, [0.3f, 0.4f])), dimensions: 2);

        await client.EmbedAsync(["a", "b"]);

        handler.LastRequestUri!.AbsolutePath.Should().Be("/v1/embeddings");
        var body = JsonNode.Parse(handler.LastRequestBody!)!;
        body["model"]!.GetValue<string>().Should().Be("bge-m3");
        var input = body["input"]!.AsArray();
        input.Should().HaveCount(2);
        input[0]!.GetValue<string>().Should().Be("a");
        input[1]!.GetValue<string>().Should().Be("b");
    }

    [Fact]
    public async Task EmbedAsync_ReadsVectors_OrderedByIndex()
    {
        // The server may return entries out of order - index decides.
        var (client, _) = NewClient(
            ResponseJson((1, [0.25f, 0.25f]), (0, [0.5f, 0.5f])), dimensions: 2);

        var vectors = await client.EmbedAsync(["a", "b"]);

        vectors.Should().HaveCount(2);
        vectors[0][0].Should().BeApproximately(0.5f, 1e-6f); // index 0 first
        vectors[1][0].Should().BeApproximately(0.25f, 1e-6f);
    }

    [Fact]
    public async Task EmbedAsync_EmptyBatch_ShortCircuitsWithoutARequest()
    {
        var (client, handler) = NewClient(ResponseJson(), dimensions: 2);

        var vectors = await client.EmbedAsync([]);

        vectors.Should().BeEmpty();
        handler.LastRequestUri.Should().BeNull();
    }

    [Fact]
    public async Task EmbedAsync_WrongDimension_Throws()
    {
        var (client, _) = NewClient(ResponseJson((0, [0.1f, 0.2f, 0.3f])), dimensions: 1024);

        var act = () => client.EmbedAsync(["a"]);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("dimension");
    }

    [Fact]
    public async Task EmbedAsync_WrongCount_Throws()
    {
        var (client, _) = NewClient(ResponseJson((0, [0.1f, 0.2f])), dimensions: 2);

        var act = () => client.EmbedAsync(["a", "b"]);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task EmbedAsync_UpstreamError_SurfacesAsHttpRequestException()
    {
        // The port's error contract: upstream failures keep arriving as
        // HttpRequestException (previously via EnsureSuccessStatusCode).
        var (client, _) = NewClient(
            """{"error":{"message":"boom"}}""", dimensions: 2, HttpStatusCode.InternalServerError);

        var act = () => client.EmbedAsync(["a"]);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
