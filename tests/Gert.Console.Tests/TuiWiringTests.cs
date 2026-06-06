using FluentAssertions;
using Gert.Console;
using Gert.Service.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Console.Tests;

/// <summary>
/// The TUI DI layer (U16, <see cref="ConsoleHostBuilder.AddGertConsoleTui"/>):
/// the local file tools must be resolvable AND present in the
/// <see cref="ToolRegistry"/> singleton — the planner offers
/// requested ∩ conversation ∩ entitlement ∩ <b>registry</b>, so a registry
/// missing the local ids silently drops the file tools.
/// </summary>
public sealed class TuiWiringTests
{
    private static ServiceProvider BuildProvider(string workspace)
    {
        // A real DataRoot: resolving the scoped ITool set instantiates the
        // built-in tools, whose dependencies (object store) validate options.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataRoot"] = workspace,
                ["Storage:ExpectedIssuer"] = LocalUserContext.ConsoleIssuer,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGertConsole(configuration);
        services.AddGertConsoleTui(workspace);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Registry_contains_the_builtin_and_local_tool_ids()
    {
        using var provider = BuildProvider(Path.GetTempPath());

        var registry = provider.GetRequiredService<ToolRegistry>();

        registry.AllIds.Should().Contain(["rag", "search", "sandbox", "todo", "clock"]);
        registry.AllIds.Should().Contain(ConsoleHostBuilder.LocalToolIds);
    }

    [Fact]
    public void Local_user_is_entitled_to_the_file_tools()
    {
        using var provider = BuildProvider(Path.GetTempPath());

        // LocalUserContext grants AllIds — with the superset registry that now
        // includes the file tools.
        var user = provider.GetRequiredService<Gert.Service.IUserContext>();

        user.AllowedTools.Should().Contain(ConsoleHostBuilder.LocalToolIds);
    }

    [Fact]
    public void File_tools_resolve_in_a_scope_with_the_workspace()
    {
        using var provider = BuildProvider(Path.GetTempPath());
        using var scope = provider.CreateScope();

        var tools = scope.ServiceProvider.GetServices<ITool>().Select(t => t.Id).ToList();

        tools.Should().Contain(ConsoleHostBuilder.LocalToolIds);
    }

    [Fact]
    public void Cli_wiring_without_the_tui_layer_keeps_the_builtin_registry()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGertConsole(configuration);
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ToolRegistry>();

        registry.AllIds.Should().NotContain(ConsoleHostBuilder.LocalToolIds);
    }
}
