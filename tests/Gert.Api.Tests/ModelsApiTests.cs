using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Gert.Model;
using Gert.Model.Json;
using Gert.Testing;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// <c>GET /api/models</c> — the operator catalog (<c>Gert:Models</c>) is served
/// as-is; with no catalog configured the single vLLM chat model is surfaced so
/// the picker always has one real option (rest-api.md § models).
/// </summary>
public sealed class ModelsApiTests : IClassFixture<GertApiFactory>
{
    private static readonly JsonSerializerOptions Json = GertJsonOptions.Default;

    private readonly GertApiFactory _factory;

    public ModelsApiTests(GertApiFactory factory) => _factory = factory;

    private static HttpClient Authed(GertApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.TokenFor("user"));
        return client;
    }

    [Fact]
    public async Task Models_without_catalog_fall_back_to_the_configured_chat_model()
    {
        var client = Authed(_factory);

        var models = await client.GetFromJsonAsync<IReadOnlyList<ModelInfo>>("/api/models", Json);

        models.Should().ContainSingle();
        models![0].Id.Should().Be("default"); // Gert:Vllm:ChatModelId default
        models[0].Default.Should().BeTrue();
        models[0].SupportsTools.Should().BeTrue();
    }

    [Fact]
    public async Task Models_with_catalog_return_the_configured_entries()
    {
        using var factory = new GertApiFactory();
        using var client = factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Gert:Models:0:Id", "qwen3-27b-fp8-mtp");
                b.UseSetting("Gert:Models:0:Name", "Qwen3-27B FP8");
                b.UseSetting("Gert:Models:0:Default", "true");
                b.UseSetting("Gert:Models:0:Endpoint", ":8001");
                b.UseSetting("Gert:Models:0:Capabilities:0", "tools");
                b.UseSetting("Gert:Models:0:Capabilities:1", "vision");
                b.UseSetting("Gert:Models:0:Context", "131072");
                b.UseSetting("Gert:Models:1:Id", "echo-only");
                b.UseSetting("Gert:Models:1:Name", "Echo Server");
                b.UseSetting("Gert:Models:1:Fast", "true");
                b.UseSetting("Gert:Models:1:Capabilities:0", "text only");
                b.UseSetting("Gert:Models:1:Context", "0");
            })
            .CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.TokenFor("user"));

        var models = await client.GetFromJsonAsync<IReadOnlyList<ModelInfo>>("/api/models", Json);

        models.Should().HaveCount(2);
        models![0].Should().BeEquivalentTo(new ModelInfo
        {
            Id = "qwen3-27b-fp8-mtp",
            Name = "Qwen3-27B FP8",
            Default = true,
            Capabilities = ["tools", "vision"],
            Context = 131072,
            Endpoint = ":8001",
        });
        models[0].SupportsTools.Should().BeTrue();
        models[1].Fast.Should().BeTrue();
        models[1].Default.Should().BeFalse();
        models[1].SupportsTools.Should().BeFalse(); // declared caps without "tools" gate
    }
}
