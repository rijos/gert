using FluentAssertions;
using Gert.Model.Dtos;
using Gert.Console.Tui.State;
using Gert.Service;
using Gert.Service.External;
using Gert.Testing;
using Gert.Testing.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// The sidebar model over the real services + fakes (U16): refresh lists the
/// default project and its conversations; rename/delete round-trip; creating
/// a project switches into it.
/// </summary>
public sealed class SidebarPresenterTests : IAsyncLifetime
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

    private async Task<string> CreateConversationAsync(string title)
    {
        await using var scope = _provider.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
        var conversation = await gert.Conversations.CreateAsync(
            "default", new CreateConversationRequest { Title = title });
        return conversation.Id;
    }

    [Fact]
    public async Task Refresh_lists_the_default_project_and_its_conversations()
    {
        var id = await CreateConversationAsync("hello world");
        var presenter = new SidebarPresenter(_provider);
        var changed = 0;
        presenter.Changed += () => changed++;

        await presenter.RefreshAsync();

        presenter.Projects.Should().Contain(p => p.Id == "default");
        presenter.Conversations.Should().Contain(c => c.Id == id && c.Title == "hello world");
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Open_returns_the_thread_and_selects_it()
    {
        var id = await CreateConversationAsync("openable");
        var presenter = new SidebarPresenter(_provider);

        var thread = await presenter.OpenAsync(id);

        thread.Should().NotBeNull();
        thread!.Conversation.Id.Should().Be(id);
        presenter.SelectedConversationId.Should().Be(id);
    }

    [Fact]
    public async Task Rename_round_trips()
    {
        var id = await CreateConversationAsync("old name");
        var presenter = new SidebarPresenter(_provider);

        await presenter.RenameAsync(id, "new name");

        presenter.Conversations.Single(c => c.Id == id).Title.Should().Be("new name");
    }

    [Fact]
    public async Task Delete_removes_and_deselects()
    {
        var id = await CreateConversationAsync("doomed");
        var presenter = new SidebarPresenter(_provider);
        await presenter.OpenAsync(id);

        await presenter.DeleteAsync(id);

        presenter.Conversations.Should().NotContain(c => c.Id == id);
        presenter.SelectedConversationId.Should().BeNull();
    }

    [Fact]
    public async Task Creating_a_project_switches_into_it()
    {
        var presenter = new SidebarPresenter(_provider);

        await presenter.CreateProjectAsync("side project");

        presenter.Pid.Should().NotBe("default");
        presenter.Projects.Should().Contain(p => p.Name == "side project");
        presenter.Conversations.Should().BeEmpty();
        presenter.SelectedConversationId.Should().BeNull();
    }
}
