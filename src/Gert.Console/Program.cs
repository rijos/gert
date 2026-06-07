// Gert.Console host — a CLI driver over the SAME services as Gert.Api, with a
// single fixed local user and inline ingestion (tech-stack.md § Architecture).
// It bypasses the API/controllers, has NO Gert.Authentication reference, and runs
// no BackgroundService — ingestion is inline via the default InlineIngestionQueue.
using Gert.Console;
using Gert.Service.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "GERT_")
    .Build();

// Shared NDJSON logging (operations.md): the SAME formatter the API uses, so the
// Console host's structured lines interleave cleanly with every other process in the
// deployment. comp="console" identifies them.
using var serilog = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.WithProperty("comp", "console")
    .WriteTo.Console(new GertNdjsonFormatter())
    .CreateLogger();

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddSerilog(serilog, dispose: false));
services.AddGertConsole(configuration);

await using var provider = services.BuildServiceProvider();

// Seed the fixed local user's user.db (username + default project) before any
// command runs — the same one-time provisioning the API does at its request edge.
using (var scope = provider.CreateScope())
{
    await scope.ServiceProvider
        .GetRequiredService<Gert.Service.Provisioning.IUserProvisioner>()
        .EnsureCurrentUserAsync();
}

var app = new ConsoleApp(provider, System.Console.Out, System.Console.Error);
return await app.RunAsync(args);
