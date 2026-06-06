using Gert.Console.Tools;
using Gert.Database.Sqlite;
using Gert.External;
using Gert.Storage;
using Gert.Service;
using Gert.Database;
using Gert.Service.Storage;
using Gert.Service.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Console;

/// <summary>
/// Builds the Console host's service graph — the non-HTTP mirror of
/// <c>Gert.Api/Program.cs</c> (tech-stack.md § Architecture). The Console drives
/// the <b>same</b> services directly: a single fixed user
/// (<see cref="LocalUserContext"/>, tools = <c>"*"</c>), ingestion run
/// <b>inline</b> (the default <c>InlineIngestionQueue</c>, no Channel worker /
/// BackgroundService), and <b>no</b> <c>Gert.Authentication</c> reference — the
/// "Console must not need the API" guarantee is structural.
/// </summary>
public static class ConsoleHostBuilder
{
    /// <summary>
    /// Register the full Console service graph into <paramref name="services"/>:
    /// <see cref="ServiceCollectionExtensions.AddGertServices"/>, the storage seam
    /// (SQLite provider / object store / user store), the
    /// <see cref="LocalUserContext"/>, and the real external adapters via
    /// <c>AddGertExternal</c>. Tests call this and then <c>Replace</c> the external
    /// ports with the <c>Gert.Testing</c> fakes.
    /// <para>
    /// The inline ingestion queue is the default registered by
    /// <see cref="ServiceCollectionExtensions.AddGertServices"/> — the Console does
    /// <b>not</b> add the Channel worker, so ingestion runs synchronously on the
    /// calling thread (a document is <c>ready</c>/<c>failed</c> by the time
    /// <c>UploadAsync</c> returns).
    /// </para>
    /// </summary>
    public static IServiceCollection AddGertConsole(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Host-agnostic service layer (incl. the inline IIngestionQueue default).
        services.AddGertServices();

        // Storage seam (storage-and-data.md § lazy provisioning) — identical to the
        // Api's non-HTTP wiring.
        services.Configure<StorageOptions>(
            configuration.GetSection(StorageOptions.SectionName));
        services.Configure<SqliteVecOptions>(
            configuration.GetSection(SqliteVecOptions.SectionName));
        services.Configure<ToolOptions>(
            configuration.GetSection(ToolOptions.SectionName));
        services.TryAddSingleton<IDatabaseProvider, SqliteDatabaseProvider>();
        services.TryAddSingleton<IDatabaseHandleReleaser, SqliteHandleReleaser>();
        services.TryAddSingleton<IObjectStore, LocalObjectStore>();
        services.TryAddSingleton<IUserStore, ObjectStoreUserStore>();

        // The single fixed local user — tools = "*" via ToolRegistry.AllIds. Singleton:
        // the identity never changes for the life of the process.
        services.TryAddSingleton<IUserContext, LocalUserContext>();

        // Real outside-world adapters (vLLM / SearXNG / gVisor sandbox + isolated
        // extractor). Tests Replace these four ports with the in-process fakes.
        services.AddGertExternal(configuration);

        return services;
    }

    /// <summary>
    /// The capability ids of the TUI-only local file tools (U16). The API host
    /// never registers these — they exist only where there is a local workspace.
    /// </summary>
    public static readonly string[] LocalToolIds =
        ["read_file", "list_dir", "glob", "grep", "write_file", "edit_file", "shell"];

    /// <summary>
    /// Register the TUI extras on top of <see cref="AddGertConsole"/> (U16): the
    /// <see cref="WorkspaceRoot"/> (the directory <c>gert</c> was launched from),
    /// the local file tools, and a <see cref="ToolRegistry"/> superset so the
    /// entitlement resolver and toggle validators accept the new ids —
    /// <c>AddGertServices</c> registers an id-only registry of the built-in five,
    /// and without the replace the planner would silently drop the local tools
    /// (offered = requested ∩ conversation ∩ entitlement ∩ <b>registry</b>).
    /// CLI mode (<c>chat</c>/<c>ingest</c>) deliberately does NOT call this.
    /// </summary>
    public static IServiceCollection AddGertConsoleTui(
        this IServiceCollection services,
        string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        services.TryAddSingleton(new WorkspaceRoot(workspaceRoot));

        // The approval seam + workspace observer. Both are late-bound bridges:
        // the TUI shell attaches the dialog handler / UI marshal after the
        // provider is built (the gated tools fail safe — deny — until then).
        // Headless callers (tests) flip AutoApprove or attach a scripted handler.
        services.TryAddSingleton<TuiToolApprover>();
        services.TryAddSingleton<IToolApprover>(sp => sp.GetRequiredService<TuiToolApprover>());
        services.TryAddSingleton<Tui.State.WorkspacePresenter>();
        services.TryAddSingleton<IWorkspaceObserver>(sp => sp.GetRequiredService<Tui.State.WorkspacePresenter>());

        // Scoped like the built-in tools (resolved as IEnumerable<ITool> by the
        // turn runner's worker scope).
        services.AddScoped<ITool, ReadFileTool>();
        services.AddScoped<ITool, ListDirTool>();
        services.AddScoped<ITool, GlobTool>();
        services.AddScoped<ITool, GrepTool>();
        services.AddScoped<ITool, WriteFileTool>();
        services.AddScoped<ITool, EditFileTool>();
        services.AddScoped<ITool, ShellExecTool>();

        // Registry superset: union the already-registered id-only registry (the
        // built-in five) with the local ids. LocalUserContext grants AllIds, so
        // this is also what entitles the file tools.
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(ToolRegistry))
            ?.ImplementationInstance as ToolRegistry;
        var ids = (existing?.AllIds ?? Enumerable.Empty<string>()).Concat(LocalToolIds);
        services.Replace(ServiceDescriptor.Singleton(new ToolRegistry(ids)));

        return services;
    }
}
