using Gert.Api.Errors;
using Gert.Authentication;
using Gert.Database.Sqlite;
using Gert.Service;
using Gert.Service.Database;
using Gert.Service.Storage;
using Gert.Service.Tools;
using Microsoft.AspNetCore.Authentication.JwtBearer;

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

// --- External-world ports ----------------------------------------------------
// TODO U10: AddGertExternal() registers the real IChatModelClient / IEmbeddingClient
// / IWebSearch / ISandbox adapters. They are deliberately NOT registered here so a
// Testing host can supply fakes (GertApiFactory.AddGertFakes) — and so DI does not
// validate-on-build against missing ports.

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

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

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
