using Gert.Service.External;
using Gert.Testing.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Testing;

/// <summary>
/// Stub <see cref="WebApplicationFactory{TEntryPoint}"/> for the API integration
/// tier (testing.md §4.2, §6). It boots <c>Gert.Api</c>'s real pipeline over a
/// TestServer and swaps the <c>Gert.External</c> ports for the in-process fakes
/// via <see cref="AddGertFakes"/>, so a behaviour proven in a unit test is the
/// behaviour the HTTP tier sees.
/// </summary>
/// <remarks>
/// <para>
/// <b>U9a:</b> this is intentionally minimal. The full wiring — the generated RSA
/// key + JWKS validation (<see cref="TestTokens"/>), the temp <see cref="TempDataRoot"/>,
/// and any host-specific options — is fleshed out when the Api host and its DI are
/// in place. For now it only registers the external-world fakes, which are the part
/// of the seam that already exists.
/// </para>
/// </remarks>
public sealed class GertApiFactory : WebApplicationFactory<Program>
{
    private Action<IServiceCollection>? _configureServices;

    /// <summary>Embedding fake exposed for tests that need to compute expected vectors.</summary>
    public FakeEmbeddings Embeddings { get; } = new();

    /// <summary>Chat-model fake; replace to script a different completion set.</summary>
    public FakeChatModel ChatModel { get; set; } = new();

    /// <summary>Web-search fake.</summary>
    public FakeWebSearch WebSearch { get; set; } = new();

    /// <summary>Sandbox stub; swap for <see cref="StubSandbox.ThatThrows"/> to drive the failure path.</summary>
    public StubSandbox Sandbox { get; set; } = new();

    /// <summary>Add extra service overrides applied after the fakes are registered.</summary>
    public GertApiFactory ConfigureTestServices(Action<IServiceCollection> configure)
    {
        _configureServices = configure;
        return this;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.ConfigureServices(services =>
        {
            AddGertFakes(services);
            _configureServices?.Invoke(services);
        });
    }

    /// <summary>
    /// Swap the <c>Gert.External</c> ports for the in-process fakes — the single DI
    /// registration that makes the whole stack run against the fake outside world
    /// (testing.md §4.2). Safe to call before the host registers the real adapters;
    /// <c>Replace</c> wins regardless of order once the adapters land (U10).
    /// </summary>
    public void AddGertFakes(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IEmbeddingClient>(Embeddings));
        services.Replace(ServiceDescriptor.Singleton<IChatModelClient>(ChatModel));
        services.Replace(ServiceDescriptor.Singleton<IWebSearch>(WebSearch));
        services.Replace(ServiceDescriptor.Singleton<ISandbox>(Sandbox));

        // TODO U9a: wire TestTokens (ephemeral RSA → JWKS validation) and point
        // DataRoot at a TempDataRoot once Gert.Api's auth + storage DI exists.
    }
}
