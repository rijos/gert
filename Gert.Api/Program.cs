using Gert.Api.Errors;
using Gert.Api.Ingestion;
using Gert.Api.Security;
using Gert.Authentication;
using Gert.Database.Sqlite;
using Gert.External;
using Gert.Service;
using Gert.Service.Database;
using Gert.Service.Ingestion;
using Gert.Service.Storage;
using Gert.Service.Tools;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// --- Service layer (host-agnostic) -----------------------------------------
builder.Services.AddControllers();

// Ingestion worker (U9b): a Channel-backed queue drained by a BackgroundService so
// uploads respond 202 and extract→chunk→embed→write runs off-thread. Registered as a
// singleton BEFORE AddGertServices so its TryAdd of the inline queue no-ops, and the
// worker + the document service share the one queue (writer ↔ reader). The IUserContext
// is not needed off-thread — IngestJob carries (iss, sub, pid).
builder.Services.AddSingleton<ChannelIngestionQueue>();
builder.Services.AddSingleton<IIngestionQueue>(sp => sp.GetRequiredService<ChannelIngestionQueue>());
builder.Services.AddHostedService<IngestionWorker>();

builder.Services.AddGertServices();

// AddGertServices (U7c) registers the three built-in tools (rag/search/sandbox)
// as scoped ITool and the id-only ToolRegistry singleton the auth + validation
// layers use for entitlement/toggle id checks (auth.md § tool entitlements). The
// orchestrator resolves the tool instances via IEnumerable<ITool>; the external
// ports each tool needs (IChatModelClient / IEmbeddingClient / IWebSearch /
// ISandbox) are registered by the U10 adapters (or a Testing host's fakes).

// --- Auth (auth.md § ASP.NET Core wiring) -----------------------------------
// AddGertJwtAuth also registers IHttpContextAccessor + IUserContext (HttpUserContext).
builder.Services.AddGertJwtAuth(builder.Configuration);
builder.Services.AddGertAuthorization();

// --- Security headers + CSP (security F1, operations.md § headers) -----------
// Binds the Pocket ID origin from Auth:Authority so the CSP's connect-src lists
// exactly 'self' + the IdP.
builder.Services.AddGertSecurityHeaders(builder.Configuration);

// --- Per-user rate limiting (security F10) -----------------------------------
// Partitioned by token sub (IP fallback for anonymous). Skipped under Testing so
// the integration suite is never throttled.
var rateLimitingEnabled = !builder.Environment.IsEnvironment("Testing");
if (rateLimitingEnabled)
{
    builder.Services.AddGertRateLimiting();
}

// Brand the JwtBearer empty-body responses: 401 (no/invalid token) and 403 (not
// admin) become Gert ProblemDetails JSON via GertProblem, instead of empty bodies.
// Post-configured here so Gert.Authentication stays host-agnostic (no Api dependency).
builder.Services.PostConfigure<JwtBearerOptions>(
    JwtBearerDefaults.AuthenticationScheme,
    options =>
    {
        options.Events ??= new JwtBearerEvents();

        options.Events.OnChallenge = async context =>
        {
            // Suppress the default empty 401 body and write the branded problem.
            context.HandleResponse();
            await GertProblem.WriteAsync(
                context.HttpContext,
                StatusCodes.Status401Unauthorized,
                "Unauthorized",
                "Authentication is required to access this resource.");
        };

        options.Events.OnForbidden = context =>
            GertProblem.WriteAsync(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                "Forbidden",
                "You do not have permission to access this resource.");
    });

// --- Storage seam (storage-and-data.md § lazy provisioning) -----------------
builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<ToolOptions>(
    builder.Configuration.GetSection(ToolOptions.SectionName));
builder.Services.AddSingleton<IDatabaseProvider, SqliteDatabaseProvider>();

// Object-store seam for per-project file blobs (projects/{pid}/files/). The local
// adapter writes under UserPaths.FilesDir; a future S3 backend is a drop-in:
// S3: new IObjectStore impl, one DI registration (swap the line below).
builder.Services.AddSingleton<UserPaths>();
builder.Services.AddSingleton<IObjectStore, LocalObjectStore>();

// User/project config + directory seam (settings.json, projects/{pid}/meta.json,
// rm -rf a project/user folder, scan users/*/meta.json). Config files are direct
// file I/O in the adapter; user blobs still flow through IObjectStore. The four
// lifecycle services (Projects/Settings/Account/Admin) orchestrate this port.
builder.Services.AddSingleton<IUserStore, FileSystemUserStore>();

// --- External-world ports ----------------------------------------------------
// U10: AddGertExternal registers the real IChatModelClient / IEmbeddingClient /
// IWebSearch / ISandbox adapters (vLLM/SearXNG over IHttpClientFactory + Polly,
// gVisor sandbox) plus the isolated pdf/docx extractor leaf, with options bound from
// config and secrets from env/user-secrets (F8). The Testing host's
// GertApiFactory.AddGertFakes() Replaces the four ports afterwards, so the fakes win
// in tests regardless of order.
builder.Services.AddGertExternal(builder.Configuration);

// One consistent, Gert-branded ProblemDetails contract: stamp every problem with
// the service marker + a traceId (Change B). The customizer runs for framework-
// produced problems and is reused by GertProblem.WriteAsync for the hand-written ones.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
        GertProblem.Stamp(ctx.ProblemDetails, ctx.HttpContext));

// 400: a service-layer ValidationException → branded ProblemDetails listing field errors.
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();

var app = builder.Build();

// Map exceptions (e.g. ValidationException from chat phase 1) to branded problems.
app.UseExceptionHandler();

// HSTS in non-Development (security F9). We assume HTTPS via the TLS-terminating
// proxy; we do NOT force a redirect in Testing/Dev so the TestServer (plain HTTP)
// and local dev still work.
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseHsts();
}

// Strict CSP + security headers on HTML responses (security F1). Runs before the
// static/SPA shell is served so index.html (and the client-route fallback) carry them.
app.UseGertSecurityHeaders();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// Per-user rate limiting (F10) — applied to the controller surface below. Absent in
// Testing (the limiter services are not registered there).
if (rateLimitingEnabled)
{
    app.UseRateLimiter();
}

var controllers = app.MapControllers();
if (rateLimitingEnabled)
{
    controllers.RequireRateLimiting(RateLimiting.PerUserPolicy);
}

// Liveness probe — the one anonymous endpoint (auth.md authorization matrix).
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();

// Unknown /api/* paths must 404 as a Gert-branded ProblemDetails, never an empty
// body and never the SPA shell. This fallback is registered before the file
// fallback and is more specific, so it wins for any /api/... that matched no
// controller (tech-stack.md single origin).
app.MapFallback("/api/{**rest}", (HttpContext http) =>
        GertProblem.WriteAsync(
            http,
            StatusCodes.Status404NotFound,
            "Not Found",
            "No such API endpoint."))
    .AllowAnonymous();

// SPA fallback: client-side routes resolve to index.html, while /api/* (above)
// and /healthz are left to the API (tech-stack.md § static SPA hosting).
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Exposed for WebApplicationFactory-based integration tests (U9a).
public partial class Program;
