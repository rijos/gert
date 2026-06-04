using FluentAssertions;
using Gert.Console;
using Gert.Model;
using Gert.Service.External;
using Gert.Testing;
using Gert.Testing.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// Drive the Console's DI graph (<see cref="ConsoleHostBuilder.AddGertConsole"/>)
/// with the <c>Gert.Testing</c> fakes over a temp <c>DataRoot</c> (testing.md §7):
/// the same services the API uses render a chat stream and ingest a sample inline
/// to <see cref="DocumentStatus.Ready"/>. Plus the structural guarantee: the
/// Console assembly has no <c>Gert.Authentication</c> reference.
/// </summary>
public sealed class ConsoleWiringTests
{
    [Fact]
    public async Task Wired_services_render_a_chat_stream_to_stdout()
    {
        await using var root = new TempDataRoot();
        await using var provider = BuildProvider(root);
        await ProvisionAsync(provider);

        var output = new StringWriter();
        var error = new StringWriter();
        var app = new ConsoleApp(provider, output, error);

        // No fixture matches → the FakeChatModel streams "Echo: <message>".
        var exit = await app.RunAsync(["chat", "ping from the console"]);

        exit.Should().Be(0);
        output.ToString().Should().Contain("Echo: ping from the console");
        output.ToString().Should().Contain("[done]");
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Inline_ingestion_of_a_sample_file_ends_ready()
    {
        await using var root = new TempDataRoot();
        await using var provider = BuildProvider(root);
        await ProvisionAsync(provider);

        var sample = Path.Combine(root.Path, "sample.txt");
        await File.WriteAllTextAsync(
            sample,
            "The quarterly report shows revenue grew sharply across every region.");

        var output = new StringWriter();
        var error = new StringWriter();
        var app = new ConsoleApp(provider, output, error);

        var exit = await app.RunAsync(["ingest", sample]);

        exit.Should().Be(0);
        // Inline ingestion runs synchronously, so the printed status is terminal.
        output.ToString().Should().Contain("sample.txt: Ready");
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_input_is_rejected_on_the_console_path_too()
    {
        await using var root = new TempDataRoot();
        await using var provider = BuildProvider(root);
        await ProvisionAsync(provider);

        var output = new StringWriter();
        var error = new StringWriter();
        var app = new ConsoleApp(provider, output, error);

        // An empty message is rejected by the SAME service-layer validator the API
        // uses (testing.md §7: the guarantee is service-layer-structural). The CLI's
        // empty-arg guard would catch "" first, so pass whitespace the validator rejects.
        var exit = await app.RunAsync(["chat", "   "]);

        exit.Should().Be(1);
        error.ToString().Should().Contain("error:");
    }

    [Fact]
    public void Console_assembly_does_not_reference_Gert_Authentication()
    {
        var referenced = typeof(LocalUserContext).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name);

        referenced.Should().NotContain("Gert.Authentication");
    }

    /// <summary>
    /// Build the Console graph with fakes swapping the four external ports, over a
    /// temp DataRoot whose ExpectedIssuer matches <see cref="LocalUserContext"/>.
    /// </summary>
    private static ServiceProvider BuildProvider(TempDataRoot root)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataRoot"] = root.Path,
                ["Storage:ExpectedIssuer"] = LocalUserContext.ConsoleIssuer,
                ["Tools:DefaultGrant:0"] = "rag",
                ["Tools:DefaultGrant:1"] = "search",
                ["Tools:DefaultGrant:2"] = "sandbox",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGertConsole(configuration);

        // Swap the real external adapters for the in-process fakes (testing.md §4.2).
        services.Replace(ServiceDescriptor.Singleton<IChatModelClient>(_ => new FakeChatModel()));
        services.Replace(ServiceDescriptor.Singleton<IEmbeddingClient>(_ => new FakeEmbeddings()));
        services.Replace(ServiceDescriptor.Singleton<IWebSearch>(_ => new FakeWebSearch()));
        services.Replace(ServiceDescriptor.Singleton<ISandbox>(_ => new StubSandbox()));

        return services.BuildServiceProvider();
    }

    /// <summary>Provision the fixed local user's folder + default project (lazy gate, U5).</summary>
    private static async Task ProvisionAsync(IServiceProvider provider)
    {
        var dbProvider = provider.GetRequiredService<Gert.Service.Database.IDatabaseProvider>();
        await dbProvider.EnsureProvisionedAsync(
            LocalUserContext.ConsoleIssuer, LocalUserContext.ConsoleSubject);
    }
}
