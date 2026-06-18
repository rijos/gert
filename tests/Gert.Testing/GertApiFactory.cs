using Gert.Chat;
using Gert.Service.External;
using Gert.Storage;
using Gert.Testing.Fakes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Gert.Testing;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> for the API integration tier
/// (testing.md section 4.2, section 6). It boots <c>Gert.Api</c>'s real pipeline over a TestServer
/// and makes it self-contained and offline:
/// <list type="bullet">
///   <item>points <c>StorageOptions.DataRoot</c> at a fresh <see cref="TempDataRoot"/>
///     (exposed for assertions, disposed with the factory);</item>
///   <item>overrides JwtBearer so it validates <see cref="TestTokens"/>-minted RS256
///     tokens against the in-process public key - no Authority/JWKS fetch
///     (<see cref="ConfigureOfflineJwtBearer"/>);</item>
///   <item>swaps the adapter ports for the in-process fakes
///     (<see cref="AddGertFakes"/>).</item>
/// </list>
/// A behaviour proven in a unit test is the behaviour the HTTP tier sees.
/// </summary>
public sealed class GertApiFactory : WebApplicationFactory<Program>
{
    private readonly TempDataRoot _dataRoot = new();
    private Action<IServiceCollection>? _configureServices;

    /// <summary>RS256 token minter - its public key is wired into JwtBearer validation.</summary>
    public TestTokens Tokens { get; } = new();

    /// <summary>The throwaway DataRoot this factory points the host at (for filesystem assertions).</summary>
    public string DataRoot => _dataRoot.Path;

    /// <summary>The conventional <c>{DataRoot}/users</c> directory.</summary>
    public string UsersDir => _dataRoot.UsersDir;

    /// <summary>Embedding fake exposed for tests that need to compute expected vectors.</summary>
    public FakeEmbeddings Embeddings { get; } = new();

    /// <summary>Chat-model fake; replace to script a different completion set.</summary>
    public FakeChatModel ChatModel { get; set; } = new();

    /// <summary>Web-search fake.</summary>
    public FakeWebSearch WebSearch { get; set; } = new();

    /// <summary>Web-fetch fake (the real fetcher rightly blocks loopback mocks).</summary>
    public FakeWebFetcher WebFetcher { get; set; } = new();

    /// <summary>Sandbox stub; swap for <see cref="StubPythonSandbox.ThatThrows"/> to drive the failure path.</summary>
    public StubPythonSandbox Sandbox { get; set; } = new();

    /// <summary>Add extra service overrides applied after the fakes are registered.</summary>
    public GertApiFactory ConfigureTestServices(Action<IServiceCollection> configure)
    {
        _configureServices = configure;
        return this;
    }

    /// <summary>Mint a bearer for a standing role (<c>admin</c> | <c>user</c> | <c>limited</c>).</summary>
    public string BearerFor(string role) => $"Bearer {Tokens.MintRole(role)}";

    /// <summary>The raw (unprefixed) token for a standing role.</summary>
    public string TokenFor(string role) => Tokens.MintRole(role);

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // ConfigureJwtBearer now fail-fasts on missing Auth:Authority/Auth:Audience. The
        // offline post-configure below swaps the signing key + issuer/audience for the
        // TestTokens values, but the base configure still runs first and must find config.
        builder.UseSetting("Auth:Authority", TestTokens.DefaultIssuer);
        builder.UseSetting("Auth:Audience", TestTokens.DefaultAudience);

        builder.ConfigureServices(services =>
        {
            // Point storage at the throwaway DataRoot.
            services.Configure<StorageOptions>(o => o.DataRoot = _dataRoot.Path);

            // Validate TestTokens offline: real RS256/JWKS path, in-process key.
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                ConfigureOfflineJwtBearer);

            AddGertFakes(services);
            _configureServices?.Invoke(services);
        });
    }

    /// <summary>
    /// Rewrite the host's <see cref="JwtBearerOptions"/> to accept
    /// <see cref="TestTokens"/>-minted tokens without any network: the signing key is
    /// the test RSA public key, issuer/audience are the test values, RS256 stays
    /// pinned, and Authority/metadata discovery is disabled.
    /// </summary>
    private void ConfigureOfflineJwtBearer(JwtBearerOptions options)
    {
        options.Authority = null;
        // JwtBearer's own post-configure already built a ConfigurationManager from the
        // host's Auth:Authority; nulling Authority alone doesn't remove it, and the
        // handler would still attempt (slow, failing) OIDC metadata fetches.
        options.ConfigurationManager = null;
        options.RequireHttpsMetadata = false;

        var tvp = options.TokenValidationParameters;
        tvp.IssuerSigningKey = Tokens.SigningKey;
        tvp.IssuerSigningKeys = [Tokens.SigningKey];
        tvp.ValidIssuer = Tokens.Issuer;
        tvp.ValidAudience = Tokens.Audience;
        tvp.ValidateIssuer = true;
        tvp.ValidateAudience = true;
        tvp.ValidateIssuerSigningKey = true;
        tvp.ValidAlgorithms = ["RS256"]; // keep the F11 alg-pin
    }

    /// <summary>
    /// Swap the adapter ports (chat, embeddings, search, fetch, sandbox)
    /// for the in-process fakes - the single DI registration that makes the whole
    /// stack run against the fake outside world (testing.md section 4.2). <c>Replace</c>
    /// wins regardless of order when the real adapters are registered.
    /// </summary>
    public void AddGertFakes(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IEmbeddingClient>(Embeddings));
        services.Replace(ServiceDescriptor.Singleton<IChatClientFactory>(new FixedChatClientFactory(ChatModel)));
        services.Replace(ServiceDescriptor.Singleton<IWebSearch>(WebSearch));
        services.Replace(ServiceDescriptor.Singleton<IWebFetcher>(WebFetcher));
        services.Replace(ServiceDescriptor.Singleton<IPythonSandbox>(Sandbox));
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Tokens.Dispose();
            _dataRoot.Dispose();
        }
    }
}
