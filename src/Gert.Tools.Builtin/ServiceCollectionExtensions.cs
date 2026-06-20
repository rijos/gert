using Gert.Tools;
using Gert.Tools.Fetch;
using Gert.Tools.Sandbox;
using Gert.Tools.Search;
using Gert.Tools.Search.SearXNG;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Tools.Builtin;

/// <summary>
/// One-call DI registration for the GENERIC tool layer (tech-stack.md section Architecture):
/// the SSRF-guarded fetch plus the keyed-plugin selectors for web search
/// (<see cref="WebSearchFactory"/>, <c>Gert:Tools:Search:Type</c>) and the <c>run_python</c>
/// sandbox (<see cref="PythonSandboxFactory"/>, <c>Gert:Tools:Sandbox:Type</c>). It registers no
/// search/sandbox IMPLEMENTATION - the composition root adds the plugins it wants
/// (<c>AddGert&lt;Capability&gt;&lt;Impl&gt;</c>) and config selects which is active; the service
/// layer talks only to the ports (<see cref="IWebSearch"/>, <see cref="IWebFetcher"/>,
/// <see cref="IPythonSandbox"/>).
///
/// <para>
/// <b>Secrets (F8):</b> options bind from configuration sections; secret values arrive via
/// environment variables / <c>dotnet user-secrets</c>, never <c>appsettings.json</c>.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Register the generic search/sandbox selectors + the SSRF-guarded fetch + options.</summary>
    public static IServiceCollection AddGertTools(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The cross-cutting SearXngOptions caps bound here (not in the SearXNG plugin): the same
        // size/time/redirect knobs bound the web_fetch fetcher below, which ships regardless of
        // the selected search backend. PythonSandboxOptions are the cross-backend per-run caps.
        services.AddOptions<SearXngOptions>()
            .Bind(configuration.GetSection(SearXngOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<PythonSandboxOptions>()
            .Bind(configuration.GetSection(PythonSandboxOptions.SectionName))
            .ValidateOnStart();

        AddSearch(services, configuration);
        AddPythonSandbox(services, configuration);
        AddBuiltinTools(services);

        return services;
    }

    /// <summary>
    /// The canonical capability ids of the built-in tools -- the id-only
    /// <see cref="ToolRegistry"/> singleton is built from these, matching the
    /// <see cref="ITool.Id"/> of each registered tool. Keep in sync with the
    /// <c>AddBuiltinTools</c> registrations below; the <c>ToolRegistrationTests</c>
    /// set-equality guard fails if they drift.
    /// </summary>
    private static readonly string[] BuiltInToolIds =
        ["rag", "search", "sandbox", "todo", "clock", "make_artifact", "edit_artifact", "read_artifact", "list_artifacts", "ask_user", "fetch", "sub_agent"];

    /// <summary>
    /// Register the built-in tools as scoped <see cref="ITool"/>s, resolved by the orchestrator
    /// via <c>IEnumerable&lt;ITool&gt;</c>. Scoped to match the per-request lifetime of the host's
    /// capability surface; the tools themselves reach RAG, objects, the UI, and delegation through
    /// the <see cref="IToolHost"/> handed at call time, never through DI - so no tool here references
    /// the service layer or an external provider. The id-only <see cref="ToolRegistry"/> +
    /// <c>BuiltInToolIds</c> census ship here too (tool identity belongs with the impls); the auth
    /// entitlement resolver + the tool-toggle validator depend on the registry for id checks only.
    /// <c>ToolRegistrationTests</c> guards the two lists.
    /// </summary>
    public static IServiceCollection AddBuiltinTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The id-only registry names capability ids, not impls (it never holds the per-request
        // scoped tool instances - the orchestrator resolves those via IEnumerable<ITool>).
        // Singleton so the singleton validators that take it are not captive on a scoped service.
        services.TryAddSingleton(new ToolRegistry(BuiltInToolIds));

        services.AddScoped<ITool, RagTool>();
        services.AddScoped<ITool, WebSearchTool>();
        services.AddScoped<ITool, PythonSandboxTool>();
        services.AddScoped<ITool, TodoTool>();
        services.AddScoped<ITool, ClockTool>();
        // Canvas artifact suite (make/edit/read/list): they reach the conversation's object store
        // through the host's IObjectResource at call time, so the ctor needs only validation.
        services.AddScoped<ITool, MakeArtifactTool>();
        services.AddScoped<ITool, EditArtifactTool>();
        services.AddScoped<ITool, ReadArtifactTool>();
        services.AddScoped<ITool, ListArtifactsTool>();
        // ask_user drives the host's IToolUi (the chat loop's ChatToolUi wires it
        // to the question registry + wire events); the tool itself has no deps.
        services.AddScoped<ITool, AskUserTool>();
        // web_fetch only calls the IWebFetcher port - the SSRF hardening (F5)
        // is the adapter's job, mirroring WebSearchTool.
        services.AddScoped<ITool, WebFetchTool>();
        // run_sub_agent delegates a task to a fresh nested model loop through the
        // host's IToolDelegate (the chat driver's ChatToolDelegate over IAgentLoop) -
        // the tool itself only parses/bounds the args, so it has no deps.
        services.AddScoped<ITool, SubAgentTool>();

        return services;
    }

    private static void AddSearch(IServiceCollection services, IConfiguration configuration)
    {
        // The SSRF-guarded fetcher owns its own SocketsHttpHandler (ConnectCallback is
        // the enforcement point), so it is NOT an IHttpClientFactory client - it must
        // not share a handler whose connect path we don't control.
        services.AddSingleton<SafeHttpFetcher>();

        // The web_fetch tool's port over the same guarded fetcher (F5). Registered
        // unconditionally - it needs no search backend, only the SearXngOptions caps
        // (Gert:Tools:Search size/time/redirect knobs), which always bind.
        services.AddSingleton<IWebFetcher, SafeWebFetcher>();

        // IWebSearch is a keyed plugin selected by Gert:Tools:Search:Type; the generic factory
        // resolves it (no central switch). The factory closes over this configuration so the
        // registration stays host-agnostic (no IConfiguration pulled from DI).
        services.AddSingleton(sp => new WebSearchFactory(sp, configuration));
        services.AddSingleton<IWebSearch>(sp => sp.GetRequiredService<WebSearchFactory>().Create());
    }

    private static void AddPythonSandbox(IServiceCollection services, IConfiguration configuration)
    {
        // IPythonSandbox is a keyed plugin selected by Gert:Tools:Sandbox:Type (default Monty -
        // no container infra needed); the generic factory resolves it (no central switch). The
        // factory closes over this configuration so the registration stays host-agnostic.
        services.AddSingleton(sp => new PythonSandboxFactory(sp, configuration));
        services.AddSingleton<IPythonSandbox>(sp => sp.GetRequiredService<PythonSandboxFactory>().Create());
    }
}
