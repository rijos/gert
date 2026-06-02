var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "Gert.Api");

app.Run();

// Exposed for WebApplicationFactory-based integration tests (U9a).
public partial class Program;
