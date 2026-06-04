using Gert.Api.Errors;
using Gert.Api.Ingestion;
using Gert.Api.Logging;
using Gert.Api.Security;
using Gert.Authentication;
using Gert.Database.Sqlite;
using Gert.External;
using Gert.Storage;
using Gert.Service;
using Gert.Service.Database;
using Gert.Service.Ingestion;
using Gert.Service.Observability;
using Gert.Service.Storage;
using Gert.Service.Tools;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- Shared NDJSON logging (operations.md § Logging format) -----------------
// Serilog as the host logger, emitting the shared schema (ts/level first) via the
// custom GertNdjsonFormatter. FromLogContext picks up the per-request comp/req/uid
// pushed by RequestLogContextMiddleware. Never logs tokens/raw sub/email/content.
builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("comp", "api")
    .WriteTo.Console(new GertNdjsonFormatter()));

// --- Service layer (host-agnostic) -----------------------------------------
builder.Services
    .AddControllers()
    .AddJsonOptions(o => Gert.Api.Json.GertJsonOptions.Configure(o.JsonSerializerOptions));

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

// --- DEV/TEST ONLY: trust a static dev JWKS file (testing.md §4.3) -----------
// The E2E harness (tools/smoke) mints RS256 tokens with a git-ignored dev key and
// writes the matching dev-jwks.json. When Gert:Dev:JwksPath is set AND we are NOT
// in Production, point JwtBearer's signing keys at that file so python-minted
// tokens validate offline through the SAME RS256/JWKS path prod uses for Pocket ID
// — only the key SOURCE differs. Two guards: the key is never committed, and this
// branch is inert under Production (and only fires when the env var is present).
var devJwksPath = builder.Configuration["Gert:Dev:JwksPath"];
if (!builder.Environment.IsProduction() && !string.IsNullOrWhiteSpace(devJwksPath))
{
    builder.Services.PostConfigure<JwtBearerOptions>(
        JwtBearerDefaults.AuthenticationScheme,
        options =>
        {
            // The dev JWKS lives at the REPO-ROOT .dev/jwt/ (where tokens.py writes
            // it). ContentRootPath is the Gert.Api project dir under `dotnet run`, so
            // a relative path is probed there first, then one level up (the repo root).
            string resolved;
            if (Path.IsPathRooted(devJwksPath))
            {
                resolved = devJwksPath;
            }
            else
            {
                var underContentRoot = Path.Combine(builder.Environment.ContentRootPath, devJwksPath);
                var underRepoRoot = Path.GetFullPath(
                    Path.Combine(builder.Environment.ContentRootPath, "..", devJwksPath));
                resolved = File.Exists(underContentRoot) ? underContentRoot : underRepoRoot;
            }

            if (!File.Exists(resolved))
            {
                throw new InvalidOperationException(
                    $"Gert:Dev:JwksPath set but no JWKS at '{resolved}'. " +
                    "Run `uv run python -m tools.smoke.tokens --role admin` first to generate it.");
            }

            var jwks = new Microsoft.IdentityModel.Tokens.JsonWebKeySet(File.ReadAllText(resolved));

            // Static keys, no network metadata fetch. Keep ValidateIssuer/Audience on
            // (they assert the Auth:Authority/Auth:Audience the dev tokens are stamped with).
            options.Authority = null;
            options.RequireHttpsMetadata = false;
            options.TokenValidationParameters.ValidIssuer = builder.Configuration["Auth:Authority"];
            options.TokenValidationParameters.ValidAudience = builder.Configuration["Auth:Audience"];
            options.TokenValidationParameters.IssuerSigningKeys = jwks.GetSigningKeys();
        });
}

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
builder.Services.Configure<SqliteVecOptions>(
    builder.Configuration.GetSection(SqliteVecOptions.SectionName));
builder.Services.Configure<ToolOptions>(
    builder.Configuration.GetSection(ToolOptions.SectionName));
builder.Services.AddSingleton<IDatabaseProvider, SqliteDatabaseProvider>();
// Lets the storage backend drop SQLite's pooled chat.db/rag.db handles before a
// local whole-tree delete; a server-backed adapter (e.g. Postgres) registers a no-op.
builder.Services.AddSingleton<IDatabaseHandleReleaser, SqliteHandleReleaser>();

// THE storage-backend seam: every non-database byte under a user tree (uploads,
// memory bodies, config sidecars) flows through IObjectStore. The local backend
// writes under {DataRoot}/users; an S3/Azure-Blob backend is a drop-in:
// S3: new IObjectStore impl, one DI registration (swap the line below).
builder.Services.AddSingleton<IObjectStore, LocalObjectStore>();

// User/project config + lifecycle seam (meta.json, settings.json, scope deletes,
// the admin scan) — backend-agnostic: everything goes through IObjectStore. The
// four lifecycle services (Projects/Settings/Account/Admin) orchestrate this port.
builder.Services.AddSingleton<IUserStore, ObjectStoreUserStore>();

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

// --- DEV/TEST ONLY: serve the component-unit harness (testing.md §8) ---------
// Maps tests/web/ at /tests/ so harness.html can import the real app modules on
// the same origin (/components, /state, …). Gated by Gert:Web:TestHarness AND
// non-Production, so the dev-only test surface never ships to prod.
if (!app.Environment.IsProduction() &&
    app.Configuration.GetValue<bool>("Gert:Web:TestHarness"))
{
    var harnessDir = Path.Combine(app.Environment.ContentRootPath, "..", "tests", "web");
    var fullHarnessDir = Path.GetFullPath(harnessDir);
    if (Directory.Exists(fullHarnessDir))
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(fullHarnessDir),
            RequestPath = "/tests",
        });
    }
}

app.UseAuthentication();

// After authentication so context.User is populated: push comp/req/uid onto the
// Serilog LogContext for the rest of the request (operations.md). uid is the short
// sha256(iss+sub) hash and only set when authenticated — never the raw sub.
app.UseMiddleware<RequestLogContextMiddleware>();

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
// UNCHANGED: always 200 for anonymous (the U9a WalkingSkeleton test asserts this).
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();

// Readiness probe (operations.md § Observability: "/healthz checking vLLM + SearXNG
// reachability"). Kept on a SEPARATE path so liveness stays a pure 200. Best-effort:
// a short-timeout GET against each upstream via its named HttpClient. 200 when all
// reachable, else 503 with a per-dependency map. Anonymous, like /healthz.
app.MapGet("/readyz", async (IHttpClientFactory httpFactory, CancellationToken ct) =>
    {
        var checks = await ReadinessCheck.RunAsync(httpFactory, ct);
        var ready = checks.Values.All(ok => ok);
        var payload = new { status = ready ? "ready" : "degraded", deps = checks };
        return Results.Json(
            payload,
            statusCode: ready
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable);
    })
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
