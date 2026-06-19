using Gert.Service.External;
using Gert.Service.Tools;
using Gert.Tools.Builtin;
using Gert.Tools.Fetch;
using Gert.Tools.Sandbox;
using Gert.Tools.Search;
using Gert.Tools.Search.SearXNG;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Tools;

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
    /// Register the built-in tools as scoped <see cref="ITool"/>s, resolved by the orchestrator
    /// via <c>IEnumerable&lt;ITool&gt;</c>. Scoped because <see cref="RagTool"/> depends on the
    /// per-request <see cref="Gert.Service.IUserContext"/>. The external ports, RAG index provider
    /// (<see cref="Gert.Rag.IRagIndexProvider"/>) and chat database provider
    /// (<see cref="Gert.Database.IChatDatabaseProvider"/>) are supplied by the host/adapters. The
    /// id-only <see cref="ToolRegistry"/> + <c>BuiltInToolIds</c> census stay in Gert.Service
    /// (ids, not impls); <c>ToolRegistrationTests</c> guards the two lists against drift.
    /// </summary>
    public static IServiceCollection AddBuiltinTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ITool, RagTool>();
        services.AddScoped<ITool, WebSearchTool>();
        services.AddScoped<ITool, PythonSandboxTool>();
        services.AddScoped<ITool, TodoTool>();
        services.AddScoped<ITool, ClockTool>();
        // Canvas artifact suite (make/edit/read), each ctor-injected with IChatRepository.
        services.AddScoped<ITool, MakeArtifactTool>();
        services.AddScoped<ITool, EditArtifactTool>();
        services.AddScoped<ITool, ReadArtifactTool>();
        // ask_user blocks on the ITurnQuestions singleton; scoped like the rest
        // (it reads the per-request/worker IUserContext for its TurnKey).
        services.AddScoped<ITool, AskUserTool>();
        // web_fetch only calls the IWebFetcher port - the SSRF hardening (F5)
        // is the adapter's job, mirroring WebSearchTool.
        services.AddScoped<ITool, WebFetchTool>();
        // save_memory wraps the scoped IMemoryService (which owns user context,
        // clock, and fail-closed validation).
        services.AddScoped<ITool, SaveMemoryTool>();
        // run_sub_agent delegates a task to a fresh nested model loop. It takes
        // IServiceProvider (not IEnumerable<ITool> - that would recurse its own
        // resolution) and re-resolves the delegable tools per execution.
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
