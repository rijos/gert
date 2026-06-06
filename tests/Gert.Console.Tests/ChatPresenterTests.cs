using FluentAssertions;
using Gert.Console.Tools;
using Gert.Console.Tui.State;
using Gert.Service.External;
using Gert.Service.Tools;
using Gert.Testing;
using Gert.Testing.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// The TUI's chat presenter end-to-end over the real service pipeline with
/// the <c>Gert.Testing</c> fakes (U16): send → plan → run → stream → transcript,
/// including the local-tool loop (a scripted <c>write_file</c> call that lands
/// on disk through the auto-approver).
/// </summary>
public sealed class ChatPresenterTests : IAsyncLifetime
{
    private TempDataRoot _root = null!;
    private string _workspace = null!;
    private ServiceProvider _provider = null!;

    public async ValueTask InitializeAsync()
    {
        _root = new TempDataRoot();
        _workspace = Path.Combine(Path.GetTempPath(), $"gert-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        _provider = BuildProvider(_root, _workspace, fixtures: null);
        await ProvisionAsync(_provider);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _root.DisposeAsync();
        Directory.Delete(_workspace, recursive: true);
    }

    private static ServiceProvider BuildProvider(TempDataRoot root, string workspace, Fixtures? fixtures)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataRoot"] = root.Path,
                ["Storage:ExpectedIssuer"] = LocalUserContext.ConsoleIssuer,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGertConsole(configuration);
        services.AddGertConsoleTui(workspace);

        services.Replace(ServiceDescriptor.Singleton<IChatModelClient>(
            _ => fixtures is null ? new FakeChatModel() : new FakeChatModel(fixtures)));
        services.Replace(ServiceDescriptor.Singleton<IEmbeddingClient>(_ => new FakeEmbeddings()));
        services.Replace(ServiceDescriptor.Singleton<IWebSearch>(_ => new FakeWebSearch()));
        services.Replace(ServiceDescriptor.Singleton<ISandbox>(_ => new StubSandbox()));

        return services.BuildServiceProvider();
    }

    private static async Task ProvisionAsync(IServiceProvider provider)
    {
        var dbProvider = provider.GetRequiredService<Gert.Database.IDatabaseProvider>();
        await dbProvider.EnsureProvisionedAsync(
            LocalUserContext.ConsoleIssuer, LocalUserContext.ConsoleSubject);
    }

    private ComposerState NewComposer(IServiceProvider provider)
    {
        var composer = new ComposerState();
        composer.SeedTools(provider.GetRequiredService<ToolRegistry>().AllIds);
        return composer;
    }

    [Fact]
    public async Task Send_streams_the_echo_reply_into_the_transcript()
    {
        var transcript = new ChatTranscript();
        var presenter = new ChatPresenter(_provider, transcript);

        await presenter.SendAsync("ping from the tui", NewComposer(_provider));

        presenter.ConversationId.Should().NotBeNull();
        transcript.Streaming.Should().BeFalse();
        var lines = transcript.Lines();
        lines.Should().Contain(l => l.Kind == LineKind.Body && l.Text == "ping from the tui");
        lines.Should().Contain(l => l.Kind == LineKind.Body && l.Text.StartsWith("Echo: ping from the tui"));
    }

    [Fact]
    public async Task Send_raises_conversation_created_exactly_once()
    {
        var transcript = new ChatTranscript();
        var presenter = new ChatPresenter(_provider, transcript);
        var created = new List<string>();
        presenter.ConversationCreated += created.Add;

        await presenter.SendAsync("first", NewComposer(_provider));
        await presenter.SendAsync("second", NewComposer(_provider));

        created.Should().ContainSingle().Which.Should().Be(presenter.ConversationId);
    }

    [Fact]
    public async Task Ui_marshal_is_used_for_every_transcript_mutation()
    {
        var transcript = new ChatTranscript();
        var marshalled = 0;
        var presenter = new ChatPresenter(_provider, transcript, action =>
        {
            marshalled++;
            action();
        });

        await presenter.SendAsync("ping", NewComposer(_provider));

        marshalled.Should().BeGreaterThan(2, "user add, capacity set and every event go through the marshal");
    }

    [Fact]
    public async Task Validation_rejection_surfaces_as_a_transcript_error()
    {
        var transcript = new ChatTranscript();
        var presenter = new ChatPresenter(_provider, transcript);

        await presenter.SendAsync("   ", NewComposer(_provider));

        transcript.Lines().Should().Contain(l => l.Kind == LineKind.Error);
        transcript.Streaming.Should().BeFalse();
    }

    [Fact]
    public void Stop_without_an_active_turn_is_a_noop()
    {
        var presenter = new ChatPresenter(_provider, new ChatTranscript());

        presenter.Stop().Should().BeFalse();
    }

    [Fact]
    public async Task Scripted_write_file_tool_call_lands_on_disk_via_the_auto_approver()
    {
        var fixtures = Fixtures.Parse(
            """
            {
              "fallback": "echo",
              "completions": [
                {
                  "match": "contains",
                  "when": "create hello.txt",
                  "deltas": ["Creating the file."],
                  "finish": "tool_calls",
                  "tool_call": {
                    "name": "write_file",
                    "arguments": "{\"path\":\"hello.txt\",\"content\":\"hi from the model\\n\"}"
                  },
                  "after_tool": {
                    "deltas": ["Done — hello.txt written."],
                    "finish": "stop",
                    "usage": { "completion_tokens": 6 }
                  }
                }
              ]
            }
            """);

        await using var root = new TempDataRoot();
        var workspace = Path.Combine(Path.GetTempPath(), $"gert-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            await using var provider = BuildProvider(root, workspace, fixtures);
            await ProvisionAsync(provider);

            // The TUI approver fails safe (deny) until a dialog handler is
            // attached — this test wants the auto-apply mode.
            provider.GetRequiredService<TuiToolApprover>().AutoApprove = true;

            var transcript = new ChatTranscript();
            var presenter = new ChatPresenter(provider, transcript);

            await presenter.SendAsync("please create hello.txt", NewComposer(provider));

            File.ReadAllText(Path.Combine(workspace, "hello.txt")).Should().Be("hi from the model\n");
            var lines = transcript.Lines();
            lines.Should().Contain(l => l.Kind == LineKind.ToolHeader && l.Text.Contains("write_file"));
            lines.Should().Contain(l => l.Kind == LineKind.Body && l.Text.Contains("Done — hello.txt written."));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
