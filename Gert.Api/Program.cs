using Gert.Authentication;
using Gert.Database.Sqlite;
using Gert.Service;
using Gert.Service.Database;
using Gert.Service.Tools;

var builder = WebApplication.CreateBuilder(args);

// --- Service layer (host-agnostic) -----------------------------------------
builder.Services.AddControllers();
builder.Services.AddGertServices();

// The tool registry is the one concrete seam type the auth + chat layers depend
// on (auth.md § tool entitlements). For M1 there are no registered tools (the
// no-tool path), so it is constructed over the empty ITool set; U7c adds tools.
builder.Services.AddSingleton<ToolRegistry>();

// --- Auth (auth.md § ASP.NET Core wiring) -----------------------------------
// AddGertJwtAuth also registers IHttpContextAccessor + IUserContext (HttpUserContext).
builder.Services.AddGertJwtAuth(builder.Configuration);
builder.Services.AddGertAuthorization();

// --- Storage seam (storage-and-data.md § lazy provisioning) -----------------
builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<ToolOptions>(
    builder.Configuration.GetSection(ToolOptions.SectionName));
builder.Services.AddSingleton<IDatabaseProvider, SqliteDatabaseProvider>();

// --- External-world ports ----------------------------------------------------
// TODO U10: AddGertExternal() registers the real IChatModelClient / IEmbeddingClient
// / IWebSearch / ISandbox adapters. They are deliberately NOT registered here so a
// Testing host can supply fakes (GertApiFactory.AddGertFakes) — and so DI does not
// validate-on-build against missing ports.

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Liveness probe — the one anonymous endpoint (auth.md authorization matrix).
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();

// Unknown /api/* paths must 404 as JSON, never fall through to the SPA shell.
// This fallback is registered before the file fallback and is more specific, so
// it wins for any /api/... that matched no controller (tech-stack.md single origin).
app.MapFallback("/api/{**rest}", () => Results.NotFound())
    .AllowAnonymous();

// SPA fallback: client-side routes resolve to index.html, while /api/* (above)
// and /healthz are left to the API (tech-stack.md § static SPA hosting).
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Exposed for WebApplicationFactory-based integration tests (U9a).
public partial class Program;
