// Gert.Console host — a CLI driver over the SAME services as Gert.Api, with a
// single fixed local user and inline ingestion (tech-stack.md § Architecture).
// It bypasses the API/controllers, has NO Gert.Authentication reference, and runs
// no BackgroundService — ingestion is inline via the default InlineIngestionQueue.
//
// Two modes:
//  - CLI ("chat"/"ingest"): NDJSON logs to stdout, exactly as before.
//  - TUI (no args, or "tui", on an interactive terminal): the full-screen
//    Terminal.Gui app (U16) — the local equivalent of the web SPA, plus local
//    file tools confined to the launch directory. stdout belongs to the
//    screen, so NDJSON goes to a FILE sink instead.
using Gert.Console;
using Gert.Console.Tui;
using Gert.Service.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

// TUI mode requires a real terminal: piped/redirected invocations fall through
// to the CLI path (zero args print usage; "tui" prints a friendly error).
var interactive = !System.Console.IsOutputRedirected && !System.Console.IsInputRedirected;
var tui = args is [] or ["tui"] && interactive;

var configuration = BuildConfiguration(tui);

// Shared NDJSON logging (operations.md): the SAME formatter the API uses, so the
// Console host's structured lines interleave cleanly with every other process in the
// deployment. comp="console" identifies them. In TUI mode the console sink would
// corrupt the alternate screen, so lines roll to a file under the user's temp dir.
using var serilog = (tui
        ? new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("comp", "console")
            .WriteTo.File(
                new GertNdjsonFormatter(),
                Path.Combine(Path.GetTempPath(), "gert", "tui-.ndjson"),
                rollingInterval: RollingInterval.Day)
        : new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("comp", "console")
            .WriteTo.Console(new GertNdjsonFormatter()))
    .CreateLogger();

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddSerilog(serilog, dispose: false));
services.AddGertConsole(configuration);
if (tui)
{
    // The local workspace = the directory gert was launched from (U16): the
    // file tools (read/list/glob/grep + the gated write/edit/shell set) are
    // confined to it, mirroring how a coding agent treats its checkout.
    services.AddGertConsoleTui(Directory.GetCurrentDirectory());
}

await using var provider = services.BuildServiceProvider();

if (tui)
{
    return TuiBootstrap.Run(provider);
}

var app = new ConsoleApp(provider, System.Console.Out, System.Console.Error);
return await app.RunAsync(args);

static IConfiguration BuildConfiguration(bool tui)
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables(prefix: "GERT_")
        .Build();

    // The TUI is an interactive app, not a service: when no DataRoot is
    // configured, default to the platform data dir (~/.local/share/gert) so
    // `gert` just works from any directory. The CLI keeps requiring explicit
    // configuration (it doubles as the ops tool).
    if (tui && string.IsNullOrWhiteSpace(configuration["Storage:DataRoot"]))
    {
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gert");
        return new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataRoot"] = fallback,
            })
            .Build();
    }

    return configuration;
}
