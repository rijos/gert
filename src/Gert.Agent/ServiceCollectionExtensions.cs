using Gert.Agent.Hosting;
using Gert.Agent.Loop;
using Gert.Service;
using Gert.Service.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Agent;

/// <summary>
/// DI wiring for the turn/agent execution engine (chat-and-tools.md section the tool loop):
/// the worker + queue, the planner/runner, the reusable <see cref="IAgentLoop"/>, the
/// ask_user/cancel registries, and the worker-scope <see cref="DetachedUserContext"/>.
/// Gert.Agent is the layer between the host and the service layer (host -> Gert.Agent ->
/// Gert.Service); the request-facing read side (the bus + conversation reader/streamer) stays
/// in <c>Gert.Service.Chat</c>. The host calls this right after <c>AddGertServices</c>; ports
/// the engine drives (<see cref="IUserContext"/>, the database/RAG/chat/tool adapters) are
/// supplied by the host. Uses <c>TryAdd</c> so a host may override any registration.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Gert turn/agent engine. The planner + runner are caller-bound scoped
    /// consumers of <see cref="IUserContext"/> (they read/write the requester's per-user store,
    /// so they must never outlive a scope); the loop + the cancel/question registries are
    /// process-wide singletons (the in-process queue means the addressed turn always lives here).
    /// </summary>
    public static IServiceCollection AddGertAgent(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Step-0 instructions reader - default to "no instructions" so the engine
        // is self-contained; a host that can read the user.db project registry overrides it.
        services.TryAddScoped<IProjectInstructionsReader, NullProjectInstructionsReader>();

        // The cancel registry is process-wide: the in-process queue means the
        // addressed turn always lives here, so the cancel endpoint can reach it.
        services.TryAddSingleton<ITurnCancellation, TurnCancellation>();
        // The ask_user question registry mirrors the cancel registry: the
        // waiting turn always lives in this process, so the answer endpoint
        // can reach it here (chat-and-tools.md section Ask the user).
        services.TryAddSingleton<ITurnQuestions, TurnQuestions>();

        // The planner + runner are caller-bound (they read/write the requester's per-user store
        // via IUserContext), so they must be scoped - a singleton would capture one caller's
        // identity and serve their data to everyone (a captive-dependency cross-user leak).
        services.TryAddScoped<ITurnPlanner, TurnPlanner>();
        // The reusable tool loop the chat shell (and the sub-agent / headless driver) run:
        // stateless beyond the clock, so a process-wide singleton.
        services.TryAddSingleton<IAgentLoop, AgentLoop>();
        // The agent: runs the loop on a background task behind a channel (compute in your name, in
        // the background). Stateless beyond a counter, so a process-wide singleton.
        services.TryAddSingleton<IAgent, Agent>();
        services.TryAddScoped<ITurnRunner, TurnRunner>();
        // The worker-scope IUserContext: seeded from the TurnJob before anything
        // else resolves in the scope. The host's IUserContext registration picks
        // this when there is no HttpContext (the queue seam: ITurnQueue is
        // host-registered, like IIngestionQueue).
        services.TryAddScoped<DetachedUserContext>();
        // TurnOptions get DEFAULTS here; the host binds "Gert:Turn" over them
        // (this layer stays configuration-agnostic).
        services.AddOptions<TurnOptions>();
        // ToolsOptions (per-tool bound overrides) same: empty defaults here; the host fills PerTool
        // from each registered tool's "Gert:Tools:<id>:Limits" section (it knows the tool ids).
        services.AddOptions<ToolsOptions>();
        // PromptOptions same: empty defaults; the host binds "Gert:Prompts" over them.
        // The type itself stays in Gert.Service.Chat (the admin SystemPromptInspector also
        // reads it), so the engine references it from the service layer.
        services.AddOptions<PromptOptions>();

        // Turn launcher (chat-and-tools.md section detached turns): same shape as ingestion -
        // POST plans + enqueues and responds 202; the launcher runs the turn off-thread (so
        // generation survives client disconnects), bounding concurrency with a global semaphore.
        // One singleton, shared as the message controller's ITurnQueue and registered as the
        // hosted service that cancels in-flight turns on shutdown.
        services.AddSingleton<TurnLauncher>();
        services.AddSingleton<ITurnQueue>(sp => sp.GetRequiredService<TurnLauncher>());
        services.AddHostedService(sp => sp.GetRequiredService<TurnLauncher>());

        return services;
    }
}
