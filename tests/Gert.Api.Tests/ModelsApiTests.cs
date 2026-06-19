using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Gert.Model;
using Gert.Model.Chat;
using Gert.Model.Json;
using Gert.Testing;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Gert.Api.Tests;

/// <summary>
/// <c>GET /api/models</c> - the operator provider catalog (<c>Gert:Chat:Providers</c>)
/// is served as-is; with no catalog configured the single OpenAI chat provider is
/// surfaced so the picker always has one real option (rest-api.md section models).
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

        var models = await client.GetFromJsonAsync<IReadOnlyList<ChatProviderInfo>>("/api/models", Json);

        models.Should().ContainSingle();
        models![0].Id.Should().Be("default"); // Gert:Embeddings fallback synthesizes the default provider
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
                // Gert:Chat:Providers is a map keyed by slug (the provider id); the
                // GetChildren() order is the configured document order. Endpoint is
                // surfaced from Parameters:BaseUrl.
                b.UseSetting("Gert:Chat:Providers:qwen3-27b-fp8-mtp:Name", "Qwen3-27B FP8");
                b.UseSetting("Gert:Chat:DefaultProvider", "qwen3-27b-fp8-mtp");
                b.UseSetting("Gert:Chat:Providers:qwen3-27b-fp8-mtp:Parameters:BaseUrl", ":8001");
                b.UseSetting("Gert:Chat:Providers:qwen3-27b-fp8-mtp:Capabilities:0", "tools");
                b.UseSetting("Gert:Chat:Providers:qwen3-27b-fp8-mtp:Capabilities:1", "vision");
                b.UseSetting("Gert:Chat:Providers:qwen3-27b-fp8-mtp:Context", "131072");
                b.UseSetting("Gert:Chat:Providers:echo-only:Name", "Echo Server");
                b.UseSetting("Gert:Chat:Providers:echo-only:Fast", "true");
                b.UseSetting("Gert:Chat:Providers:echo-only:Capabilities:0", "text only");
                b.UseSetting("Gert:Chat:Providers:echo-only:Context", "0");
            })
            .CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.TokenFor("user"));

        var models = await client.GetFromJsonAsync<IReadOnlyList<ChatProviderInfo>>("/api/models", Json);

        models.Should().HaveCount(2);

        // Lookup by id, not position: the in-memory config provider sorts map keys
        // (a real JSON appsettings preserves the operator's document order).
        var qwen = models!.Single(m => m.Id == "qwen3-27b-fp8-mtp");
        qwen.Should().BeEquivalentTo(new ChatProviderInfo
        {
            Id = "qwen3-27b-fp8-mtp",
            Name = "Qwen3-27B FP8",
            Default = true,
            Capabilities = ["tools", "vision"],
            Context = 131072,
            Endpoint = ":8001",
        });
        qwen.SupportsTools.Should().BeTrue();

        var echo = models!.Single(m => m.Id == "echo-only");
        echo.Fast.Should().BeTrue();
        echo.Default.Should().BeFalse();
        echo.SupportsTools.Should().BeFalse(); // declared caps without "tools" gate
    }
}
