// Gert.Console host — a CLI driver over the SAME services as Gert.Api, with a
// single fixed local user and inline ingestion (tech-stack.md § Architecture).
// It bypasses the API/controllers, has NO Gert.Authentication reference, and runs
// no BackgroundService — ingestion is inline via the default InlineIngestionQueue.
using Gert.Console;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "GERT_")
    .Build();

var services = new ServiceCollection();
services.AddLogging();
services.AddGertConsole(configuration);

await using var provider = services.BuildServiceProvider();

var app = new ConsoleApp(provider, System.Console.Out, System.Console.Error);
return await app.RunAsync(args);
