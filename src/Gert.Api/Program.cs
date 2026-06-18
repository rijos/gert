using Gert.Api.Chat;
using Gert.Api.Errors;
using Gert.Api.Ingestion;
using Gert.Api.Logging;
using Gert.Api.Security;
using Gert.Authentication;
using Gert.Chat;
using Gert.Chat.OpenAI;
using Gert.Database;
using Gert.Database.Sqlite;
using Gert.Ingestion;
using Gert.Rag;
using Gert.Rag.Sqlite;
using Gert.Service;
using Gert.Service.Chat;
using Gert.Service.Ingestion;
using Gert.Service.Observability;
using Gert.Storage.Local;
using Gert.Tools;
using Gert.Tools.Sandbox.GVisor;
using Gert.Tools.Sandbox.Monty;
using Gert.Tools.Search.SearXNG;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// --- Captive-dependency guard (principles.md per-user isolation) -------------------
// Per-user isolation rests on a service resolving the caller from the request-scoped
// IUserContext (Program.cs "IUserContext routing" below). A singleton that consumed a
// user-scoped service would capture the FIRST caller's identity and then serve their
// data to everyone - a cross-user leak, not just a lifetime bug. ValidateScopes makes
// that a hard failure in EVERY environment (the framework default only validates in
// Development), and ValidateOnBuild surfaces it at startup instead of on first request.
// The service layer's own registrations are additionally guarded by an architecture
// test (ArchitectureTests.Services_consuming_IUserContext_are_scoped); this is the
// backstop for host- and adapter-registered services the test can't see.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

// --- Request body cap (testing.md section 5 upload limits) --------------------------
// Kestrel's default MaxRequestBodySize (~28.6 MB) would 413 a legitimate upload
// before DocumentUploadValidator ever saw it. Raise the transport cap to the
// 50 MiB upload limit plus 1 MiB of headroom for multipart framing (boundaries,
// part headers), so a full-size file still reaches the validator and over-limit
// uploads get the authoritative, branded 400 - not a bare Kestrel 413.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Limits.MaxRequestBodySize =
        Gert.Service.Validation.UploadConstraints.MaxSizeBytes + 1_048_576);

// --- Bounded shutdown ---------------------------------------------------------
// Open SSE streams end themselves on ApplicationStopping (the stream endpoint
// links it), so this backstop only catches a request that ignores the
// signal - Ctrl+C must stop the host in seconds, never the framework's default
// 30 s drain.
builder.Services.Configure<HostOptions>(host =>
    host.ShutdownTimeout = TimeSpan.FromSeconds(5));

// --- Shared NDJSON logging (operations.md section Logging format) -----------------
// Serilog as the host logger, emitting the shared schema (ts/level first) via the
// custom GertNdjsonFormatter. FromLogContext picks up the per-request comp/req/uid
// pushed by RequestLogContextMiddleware. Never logs tokens/raw sub/email/content.
// Serilog owns the sink, so MS.Extensions.Logging's own level filters never
// reach it - read the standard Logging:LogLevel config ourselves so the level
// stays operator-configurable via the standard Logging:LogLevel:Default knob.
// Default is Information; appsettings keeps Microsoft.AspNetCore at Warning, so
// a Debug run stays focused on Gert instead of flooding Kestrel internals.
builder.Host.UseSerilog((context, loggerConfiguration) =>
{
    var logLevels = context.Configuration.GetSection("Logging:LogLevel");
    loggerConfiguration
        .MinimumLevel.Is(ToSerilogLevel(logLevels["Default"]))
        .Enrich.FromLogContext()
        .Enrich.WithProperty("comp", "api")
        .WriteTo.Console(new GertNdjsonFormatter());
    foreach (var category in logLevels.GetChildren())
    {
        if (category.Key != "Default" && !string.IsNullOrEmpty(category.Value))
        {
            loggerConfiguration.MinimumLevel.Override(category.Key, ToSerilogLevel(category.Value));
        }
    }
});

// --- Service layer (host-agnostic) -----------------------------------------
builder.Services
    .AddControllers()
    .AddJsonOptions(o => Gert.Model.Json.GertJsonOptions.Configure(o.JsonSerializerOptions));

// Ingestion worker: a Channel-backed queue drained by a BackgroundService so
// uploads respond 202 and extract->chunk->embed->write runs off-thread. Registered as a
// singleton BEFORE AddGertServices so its TryAdd of the inline queue no-ops, and the
// worker + the document service share the one queue (writer <-> reader). The IUserContext
// is not needed off-thread - IngestJob carries (iss, sub, pid).
builder.Services.AddSingleton<ChannelIngestionQueue>();
builder.Services.AddSingleton<IIngestionQueue>(sp => sp.GetRequiredService<ChannelIngestionQueue>());
builder.Services.AddHostedService<IngestionWorker>();

// Turn worker (chat-and-tools.md section detached turns): same shape as ingestion -
// POST plans + enqueues and responds 202; the worker drives the tool loop
// off-thread, so generation survives client disconnects. The TurnJob carries
// (iss, sub, entitlement snapshot); TurnWorker seeds DetachedUserContext per scope.
builder.Services.AddSingleton<ChannelTurnQueue>();
builder.Services.AddSingleton<ITurnQueue>(sp => sp.GetRequiredService<ChannelTurnQueue>());
builder.Services.AddHostedService<TurnWorker>();

builder.Services.AddGertServices();

// Detached turn pipeline tunables (chat-and-tools.md section detached turns). The service
// layer registers the defaults; the host binds configuration over them
// (dotnet-style-guide.md section 4 - no annotations on TurnOptions, so no
// ValidateDataAnnotations; the lane count gets an explicit predicate instead).
builder.Services.AddOptions<Gert.Service.Chat.TurnOptions>()
    .BindConfiguration("Gert:Turn")
    .Validate(o => o.MaxConcurrentTurns >= 1, "Gert:Turn:MaxConcurrentTurns must be >= 1")
    .ValidateOnStart();

// AddGertServices registers the id-only ToolRegistry singleton the auth + validation
// layers use for entitlement/toggle id checks (auth.md section tool entitlements). The
// built-in tool implementations (rag/search/sandbox/...) are registered as scoped ITool
// by the Gert.Tools adapter's AddBuiltinTools (called from AddGertTools below); the
// orchestrator resolves the tool instances via IEnumerable<ITool>. The external ports
// each tool needs (IChatModelClient / IEmbeddingClient / IWebSearch / IPythonSandbox)
// are registered by the Gert.Chat/Tools/Ingestion adapters (or a Testing host's fakes).

// --- Auth (auth.md section ASP.NET Core wiring) -----------------------------------
// AddGertJwtAuth also registers IHttpContextAccessor + IUserContext (HttpUserContext).
builder.Services.AddGertJwtAuth(builder.Configuration);
builder.Services.AddGertAuthorization();

// IUserContext routing: requests resolve the JWT-backed HttpUserContext; worker
// scopes (no HttpContext) resolve the DetachedUserContext that TurnWorker seeds
// from the job's plan-time identity + entitlement snapshot. Without this, the
// scoped tools (rag) would throw "no HTTP context" off-thread.
builder.Services.Replace(ServiceDescriptor.Scoped<IUserContext>(sp =>
    sp.GetRequiredService<IHttpContextAccessor>().HttpContext is not null
        ? ActivatorUtilities.CreateInstance<HttpUserContext>(sp)
        : sp.GetRequiredService<DetachedUserContext>()));

// --- DEV/TEST ONLY: trust a static dev JWKS file (testing.md section 4.3) -----------
// The E2E harness (tools/smoke) mints RS256 tokens with a git-ignored dev key and
// writes the matching dev-jwks.json. When Gert:Dev:JwksPath is set AND we are NOT
// in Production, point JwtBearer's signing keys at that file so python-minted
// tokens validate offline through the SAME RS256/JWKS path prod uses for Pocket ID
// - only the key SOURCE differs. Two guards: the key is never committed, and this
// branch is inert under Production (and only fires when the env var is present).
var devJwksPath = builder.Configuration["Gert:Dev:JwksPath"];
var devJwksActive = !builder.Environment.IsProduction() && !string.IsNullOrWhiteSpace(devJwksPath);
if (devJwksActive)
{
    // Non-null: devJwksActive above implies IsNullOrWhiteSpace was false, but that
    // null-state doesn't flow into the PostConfigure lambda below.
    var jwksConfigPath = devJwksPath!;
    builder.Services.PostConfigure<JwtBearerOptions>(
        JwtBearerDefaults.AuthenticationScheme,
        options =>
        {
            // The dev JWKS lives at the REPO-ROOT .dev/jwt/ (where tokens.py writes
            // it). ContentRootPath is the src/Gert.Api project dir under `dotnet run`,
            // so a relative path is probed there first, then two levels up (the repo root).
            string resolved;
            if (Path.IsPathRooted(jwksConfigPath))
            {
                resolved = jwksConfigPath;
            }
            else
            {
                var underContentRoot = Path.Combine(builder.Environment.ContentRootPath, jwksConfigPath);
                var underRepoRoot = Path.GetFullPath(
                    Path.Combine(builder.Environment.ContentRootPath, "..", "..", jwksConfigPath));
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
            // JwtBearer's own post-configure ran BEFORE this one (registration order) and
            // already built a ConfigurationManager from Auth:Authority - nulling Authority
            // alone doesn't remove it, and the handler would keep trying to fetch OIDC
            // metadata from the unresolvable dev authority (a multi-second mDNS/DNS stall
            // on the first authenticated request after every retry-backoff window).
            options.Authority = null;
            options.ConfigurationManager = null;
            options.RequireHttpsMetadata = false;
            options.TokenValidationParameters.ValidIssuer = builder.Configuration["Auth:Authority"];
            options.TokenValidationParameters.ValidAudience = builder.Configuration["Auth:Audience"];
            options.TokenValidationParameters.IssuerSigningKeys = jwks.GetSigningKeys();
        });
}

// --- Security headers + CSP (security F1, operations.md section headers) -----------
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

// --- Storage seam (storage-and-data.md section lazy provisioning) -----------------
// One AddGert<Capability><Impl> per adapter (dotnet-style-guide.md section 4). Database is a
// keyed capability-plugin like chat: AddGertDatabase wires the GENERIC engine selector
// (Gert:Database:Type) + the three provider ports; AddGertDatabaseSqlite makes the SQLite engine
// available (the keyed builder + bound Storage options). AddGertStorageLocal the local
// IObjectStore backend over the same data-root. Database + storage are independent stores - the
// database engine owns destroying its own data, the object store owns artifact bytes - and the
// service layer orchestrates a user/project delete across both (principle #5). A Postgres engine
// is a drop-in: AddGertDatabasePostgres + Gert:Database:Type=Postgres; an S3/Azure-Blob backend
// swaps AddGertStorageLocal for that backend's AddGertStorage* call.
builder.Services.AddGertDatabase(builder.Configuration);
builder.Services.AddGertDatabaseSqlite(builder.Configuration);
// RAG is its own capability-plugin (vector index, decoupled from the SQL engine):
// AddGertRag the generic engine selector (Gert:Rag:Type) + the index provider port,
// AddGertRagSqlite the sqlite-vec + FTS5 engine. A dedicated vector store is a drop-in:
// AddGertRagQdrant + Gert:Rag:Type=Qdrant.
builder.Services.AddGertRag(builder.Configuration);
builder.Services.AddGertRagSqlite(builder.Configuration);
builder.Services.AddGertStorageLocal(builder.Configuration);

// Forward-recovery for the deletion saga: on startup, finish any account deletion a previous
// run left interrupted (the deletion journal + the idempotent eraser are wired above).
builder.Services.AddHostedService<Gert.Api.Lifecycle.DeletionRecoveryService>();

// --- External-world ports ----------------------------------------------------
// One AddGert* per functionality (dotnet-style-guide.md section 4). Chat is split into the
// GENERIC layer (AddGertChat: the impl-agnostic provider catalog + keyed-plugin chat-client
// factory + IChatProviderCatalog) and the IMPLEMENTATION plugins the composition root makes
// available (AddGertChatOpenAI: the OpenAI chat-client builder + per-provider transports +
// IEmbeddingClient). Configuration selects which registered plugin builds each provider
// (Gert:Chat:Providers:<slug>:Type). Search and the run_python sandbox follow the same keyed
// capability-plugin pattern: AddGertTools registers the GENERIC selectors over the IWebSearch /
// IWebFetcher / IPythonSandbox ports, and the composition root makes the shipped plugins
// available (AddGertSearchSearXNG; AddGertSandboxMonty / AddGertSandboxGVisor) - configuration
// picks the active one (Gert:Tools:Search:Type / Gert:Tools:Sandbox:Type). AddGertIngestion the
// isolated pdf/docx extractor leaf. Options bind from config; secrets come from env/user-secrets
// (F8). The Testing host's GertApiFactory.AddGertFakes() Replaces the ports afterwards, so the
// fakes win in tests regardless of order.
builder.Services.AddGertChat(builder.Configuration);
builder.Services.AddGertChatOpenAI(builder.Configuration);
builder.Services.AddGertTools(builder.Configuration);
builder.Services.AddGertSearchSearXNG();
builder.Services.AddGertSandboxMonty(builder.Configuration);
builder.Services.AddGertSandboxGVisor(builder.Configuration);
builder.Services.AddGertIngestion(builder.Configuration);

// One consistent, Gert-branded ProblemDetails contract: stamp every problem with
// the service marker + a traceId (Change B). The customizer runs for framework-
// produced problems and is reused by GertProblem.WriteAsync for the hand-written ones.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
        GertProblem.Stamp(ctx.ProblemDetails, ctx.HttpContext));

// 400: a service-layer ValidationException -> branded ProblemDetails listing field errors.
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();

// 409: a turn is already streaming in this conversation (turns are serialized
// per conversation - chat-and-tools.md section detached turns).
builder.Services.AddExceptionHandler<TurnConflictExceptionHandler>();

var app = builder.Build();

// Make the dev-JWKS trust loudly visible (testing.md section 4.3): it weakens token
// validation to a static, git-ignored key file, so an operator must never see
// this line in a real deployment's logs. (The branch above is already inert
// under Production; this is the tripwire if an environment is mislabelled.)
if (devJwksActive)
{
    app.Logger.LogWarning(
        "dev static JWKS trust active (Gert:Dev:JwksPath={JwksPath}) - dev/test only, never production",
        devJwksPath);
}

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

// --- DEV/TEST ONLY: serve the component-unit harness (testing.md section 8) ---------
// Maps tests/web/ at /tests/ so harness.html can import the real app modules on
// the same origin (/components, /state, ...). Gated by Gert:Web:TestHarness AND
// non-Production, so the dev-only test surface never ships to prod.
if (!app.Environment.IsProduction() &&
    app.Configuration.GetValue<bool>("Gert:Web:TestHarness"))
{
    var harnessDir = Path.Combine(app.Environment.ContentRootPath, "..", "..", "tests", "web");
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
// sha256(iss+sub) hash and only set when authenticated - never the raw sub.
app.UseMiddleware<RequestLogContextMiddleware>();

app.UseAuthorization();

// First touch from a valid identity materialises their user.db (username + default
// project) before any controller reads it. The databases self-provision
// on open; this only seeds the descriptive, product-level state.
app.UseMiddleware<Gert.Api.UserProvisioningMiddleware>();

// Per-user rate limiting (F10) - applied to the controller surface below. Absent in
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

// Liveness probe - the one anonymous endpoint (auth.md authorization matrix).
// UNCHANGED: always 200 for anonymous (the WalkingSkeleton test asserts this).
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();

// Readiness probe (operations.md section Observability: "/healthz checking vLLM + SearXNG
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
// and /healthz are left to the API (tech-stack.md section static SPA hosting).
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Map a Microsoft.Extensions.Logging level name (the standard Logging:LogLevel
// config) onto Serilog's LogEventLevel; unset/unknown falls back to Information.
static LogEventLevel ToSerilogLevel(string? level) =>
    Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed)
        ? parsed switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            LogLevel.None => LogEventLevel.Fatal,
            _ => LogEventLevel.Information,
        }
        : LogEventLevel.Information;

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
