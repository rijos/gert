using FluentAssertions;
using Gert.Console.Tui.State;
using Gert.Model;
using Gert.Model.Dtos;
using Gert.Service.External;
using Gert.Testing;
using Gert.Testing.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// The knowledge + settings models over the real services with fakes (U16):
/// inline upload ends Ready, memory round-trips, settings partial-update
/// merges.
/// </summary>
public sealed class KnowledgeAndSettingsPresenterTests : IAsyncLifetime
{
    private TempDataRoot _root = null!;
    private ServiceProvider _provider = null!;

    public async ValueTask InitializeAsync()
    {
        _root = new TempDataRoot();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataRoot"] = _root.Path,
                ["Storage:ExpectedIssuer"] = LocalUserContext.ConsoleIssuer,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGertConsole(configuration);
        services.Replace(ServiceDescriptor.Singleton<IChatModelClient>(_ => new FakeChatModel()));
        services.Replace(ServiceDescriptor.Singleton<IEmbeddingClient>(_ => new FakeEmbeddings()));
        services.Replace(ServiceDescriptor.Singleton<IWebSearch>(_ => new FakeWebSearch()));
        services.Replace(ServiceDescriptor.Singleton<ISandbox>(_ => new StubSandbox()));
        _provider = services.BuildServiceProvider();

        var dbProvider = _provider.GetRequiredService<Gert.Database.IDatabaseProvider>();
        await dbProvider.EnsureProvisionedAsync(
            LocalUserContext.ConsoleIssuer, LocalUserContext.ConsoleSubject);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _root.DisposeAsync();
    }

    [Fact]
    public async Task Upload_ends_ready_and_lists_with_a_decoded_name()
    {
        var sample = Path.Combine(_root.Path, "notes.txt");
        await File.WriteAllTextAsync(sample, "The migration plan ships in three phases this quarter.");
        var presenter = new KnowledgePresenter(_provider);

        var document = await presenter.UploadAsync(sample);

        document.Status.Should().Be(DocumentStatus.Ready);
        var listed = await presenter.ListDocumentsAsync();
        listed.Should().ContainSingle();
        KnowledgePresenter.DisplayName(listed[0]).Should().Be("notes.txt");

        await presenter.DeleteDocumentAsync(document.Id);
        (await presenter.ListDocumentsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Memory_round_trips()
    {
        var presenter = new KnowledgePresenter(_provider);

        var entry = await presenter.UpsertMemoryAsync("prefs", "Prefers tabs over spaces.");
        (await presenter.ListMemoryAsync()).Should().ContainSingle(m => m.Title == "prefs");

        await presenter.DeleteMemoryAsync(entry.Id);
        (await presenter.ListMemoryAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Settings_partial_update_merges()
    {
        var presenter = new SettingsPresenter(_provider);

        var updated = await presenter.SaveAsync(new UpdateSettingsRequest
        {
            ReplyLanguage = "Dutch",
            ModelParams = new Dictionary<string, GenerationParams>
            {
                ["qwen"] = new GenerationParams { Temperature = 0.7, MaxTokens = 2048 },
            },
        });

        updated.ReplyLanguage.Should().Be("Dutch");

        var reloaded = await presenter.LoadAsync();
        reloaded.ReplyLanguage.Should().Be("Dutch");
        reloaded.ModelParams!["qwen"].Temperature.Should().Be(0.7);
        reloaded.ModelParams["qwen"].MaxTokens.Should().Be(2048);
    }
}
