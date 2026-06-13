using FluentValidation;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Service.Account;
using Gert.Service.Admin;
using Gert.Service.Chat;
using Gert.Service.Conversations;
using Gert.Service.Documents;
using Gert.Service.Ingestion;
using Gert.Service.Projects;
using Gert.Service.Tools;
using Gert.Service.Validation;
using Gert.Service.Validation.Validators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Service;

/// <summary>
/// DI wiring for the host-agnostic service layer (tech-stack.md section Architecture).
/// Registers the granular services, the aggregate <see cref="IGertServices"/>
/// hub, and the validation seam. The host/adapters supply the ports the services
/// depend on - <see cref="IUserContext"/> (auth host), the database providers
/// (<see cref="Database.IUserDatabaseProvider"/> / <see cref="Database.IChatDatabaseProvider"/>
/// / <see cref="Database.IRagDatabaseProvider"/>), and
/// <see cref="External.IChatModelClient"/> (Gert.External) - so this method does
/// not register them.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Gert service layer. Services are <b>scoped</b> - they depend
    /// on the per-request <see cref="IUserContext"/>, so their lifetime must not
    /// outlive a request. Uses <c>TryAdd</c> so a host may override any registration.
    /// </summary>
    public static IServiceCollection AddGertServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Validation seam -- the fail-closed FluentValidation-backed provider
        // plus a validator for every request DTO a service accepts. A DTO with no
        // registered IValidator<T> makes the provider throw (principle #6); the
        // reflection meta-test keeps that throw unreachable in production.
        AddValidation(services);

        // All service-layer time flows through the injected TimeProvider
        // (dotnet-style-guide.md section 5), so tests pin the instant. Singleton:
        // process-wide wall clock; TryAdd so tests override with a fake.
        services.TryAddSingleton(TimeProvider.System);

        // Step-0 instructions reader - default to "no instructions" so the service
        // layer is self-contained; a host that can read the user.db project
        // registry overrides it.
        services.TryAddScoped<IProjectInstructionsReader, NullProjectInstructionsReader>();

        // Granular services.
        services.TryAddScoped<IConversationService, ConversationService>();

        // Detached turn pipeline (chat-and-tools.md section detached turns): the bus is a
        // process-wide singleton (live delivery is per-process; the DB is the
        // cross-instance truth); the reader is scoped (per-request IUserContext).
        services.TryAddSingleton<Chat.Bus.IConversationBus, Chat.Bus.ConversationBus>();
        // The cancel registry is process-wide for the same reason the bus is:
        // the in-process queue means the addressed turn always lives here.
        services.TryAddSingleton<ITurnCancellation, TurnCancellation>();
        // The ask_user question registry mirrors the cancel registry: the
        // waiting turn always lives in this process, so the answer endpoint
        // can reach it here (chat-and-tools.md section Ask the user).
        services.TryAddSingleton<ITurnQuestions, TurnQuestions>();
        services.TryAddScoped<IConversationReader, ConversationReader>();
        services.TryAddScoped<IConversationStreamer, ConversationStreamer>();
        services.TryAddScoped<ITurnPlanner, TurnPlanner>();
        services.TryAddScoped<ITurnRunner, TurnRunner>();
        // The worker-scope IUserContext: seeded from the TurnJob before anything
        // else resolves in the scope. The host's IUserContext registration picks
        // this when there is no HttpContext (the queue seam: ITurnQueue is
        // host-registered, like IIngestionQueue).
        services.TryAddScoped<DetachedUserContext>();
        // TurnOptions get DEFAULTS here; the host binds "Gert:Turn" over them
        // (this layer stays configuration-agnostic).
        services.AddOptions<TurnOptions>();

        services.TryAddScoped<IDocumentService, DocumentService>();
        services.TryAddScoped<IArtifactService, ArtifactService>();
        services.TryAddScoped<IMemoryService, MemoryService>();
        services.TryAddScoped<IProjectService, ProjectService>();
        services.TryAddScoped<ISettingsService, SettingsService>();
        services.TryAddScoped<IAccountService, AccountService>();
        services.TryAddScoped<IAdminService, AdminService>();
        services.TryAddScoped<ISystemPromptInspector, SystemPromptInspector>();
        services.TryAddScoped<Provisioning.IUserProvisioner, Provisioning.UserProvisioner>();

        // Ingestion - the extract -> chunk -> embed -> write pipeline and its
        // ports. Files are read/written ONLY via IObjectStore (host-registered) and
        // text-extraction is behind ITextExtractor so Gert.External swaps the hardened pdf/docx
        // extractor in with one registration. The default queue runs ingestion inline;
        // The API host replaces it with a Channel-backed queue + BackgroundService (responds 202).
        AddIngestion(services);

        // Tools -- each ITool is registered so the ToolRegistry is populated
        // from IEnumerable<ITool>. They are scoped: RagTool depends on the
        // per-request IUserContext, so a tool's lifetime must not outlive a request.
        AddTools(services);

        // Aggregate hub.
        services.TryAddScoped<IGertServices, GertServices>();

        return services;
    }

    /// <summary>
    /// The canonical capability ids of the built-in tools -- the id-only
    /// <see cref="ToolRegistry"/> singleton is built from these, matching the
    /// <see cref="ITool.Id"/> of each registered tool. Keep in sync with
    /// <see cref="AddTools"/>.
    /// </summary>
    private static readonly string[] BuiltInToolIds =
        ["rag", "search", "sandbox", "todo", "clock", "make_artifact", "edit_artifact", "read_artifact", "ask_user", "fetch", "memory", "sub_agent"];

    /// <summary>
    /// DI key for the per-type leaf <see cref="ITextExtractor"/>s the
    /// <see cref="CompositeTextExtractor"/> composes. Keying them keeps the composite
    /// (registered as the plain <see cref="ITextExtractor"/>) out of its own
    /// enumeration. The pdf/docx extractor (Gert.External) registers under the same
    /// key, so it is <c>public</c> for that adapter to reference.
    /// </summary>
    public const string LeafExtractorKey = "leaf";

    /// <summary>
    /// Register the built-in tools as scoped <see cref="ITool"/>s so the
    /// orchestrator resolves them via <c>IEnumerable&lt;ITool&gt;</c>. Scoped
    /// because <see cref="Tools.RagTool"/> depends on the per-request
    /// <see cref="IUserContext"/>. The external ports each tool needs
    /// (<see cref="External.IEmbeddingClient"/>, <see cref="External.IWebSearch"/>,
    /// <see cref="External.IPythonSandbox"/>) and the <see cref="Database.IRagDatabaseProvider"/>
    /// are supplied by the host/adapters (Gert.External / a database adapter).
    /// </summary>
    private static void AddTools(IServiceCollection services)
    {
        services.AddScoped<ITool, RagTool>();
        services.AddScoped<ITool, WebSearchTool>();
        services.AddScoped<ITool, PythonSandboxTool>();
        services.AddScoped<ITool, TodoTool>();
        services.AddScoped<ITool, ClockTool>();
        // The canvas artifact suite (make/edit/read) - model-driven file creation
        // and in-place iteration; each is ctor-injected with IChatRepository.
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
    }

    /// <summary>
    /// Wire the ingestion pipeline. Chunking is stateless (a singleton
    /// options record); the per-type extractors are registered as
    /// <see cref="ITextExtractor"/> and composed by <see cref="CompositeTextExtractor"/>
    /// (the one the pipeline depends on). The pipeline + queue are scoped because the
    /// queue's inline path resolves per-request collaborators; the API host overrides the queue
    /// with its Channel-backed singleton + a BackgroundService.
    /// </summary>
    private static void AddIngestion(IServiceCollection services)
    {
        services.TryAddSingleton(ChunkingOptions.Default);

        // The per-type leaf extractors are registered under the "leaf" key so the
        // composite can enumerate them without the composite (registered as the plain
        // ITextExtractor below) re-entering its own resolution. Gert.External registers the
        // pdf/docx extractor with the same key - one line, no pipeline change.
        services.AddKeyedSingleton<ITextExtractor, PlainTextExtractor>(LeafExtractorKey);

        // The pipeline depends on the plain ITextExtractor -> the composite, which
        // routes to the keyed leaves.
        services.TryAddSingleton<ITextExtractor>(sp =>
            new CompositeTextExtractor(sp.GetKeyedServices<ITextExtractor>(LeafExtractorKey)));

        services.TryAddScoped<IIngestionService, IngestionService>();
        services.TryAddScoped<IIngestionQueue, InlineIngestionQueue>();
    }

    /// <summary>
    /// Wire the fail-closed validation provider and register every validator.
    /// Validators are registered <b>both</b> as their concrete type (so a parent
    /// validator can take a child via constructor injection for <c>SetValidator</c>)
    /// and as <c>IValidator&lt;T&gt;</c> (so <see cref="FluentValidationProvider"/>
    /// resolves them and the meta-test can discover them). Validators are stateless,
    /// so they are singletons.
    /// </summary>
    private static void AddValidation(IServiceCollection services)
    {
        services.TryAddScoped<IValidationProvider, FluentValidationProvider>();

        // The tool validator + the entitlement resolver depend on the registry for
        // id checks only (Contains / Normalize / AllIds), so it is an id-only
        // singleton built from the registered tools' canonical ids - it never holds
        // the per-request scoped tool instances (which the orchestrator resolves via
        // IEnumerable<ITool>). Registered as a singleton so the singleton validators
        // that take it are not captive-dependent on a scoped service.
        services.TryAddSingleton(new ToolRegistry(BuiltInToolIds));

        // Shared / nested validators - needed by concrete type for SetValidator.
        AddValidator<ToolToggles, ToolTogglesValidator>(services);
        AddValidator<ProjectDefaults, ProjectDefaultsValidator>(services);
        AddValidator<Gert.Model.Chat.MessageAttachment, MessageAttachmentValidator>(services);

        // Request DTOs every service method accepts.
        AddValidator<SendMessageRequest, SendMessageRequestValidator>(services);
        AddValidator<CreateConversationRequest, CreateConversationRequestValidator>(services);
        AddValidator<UpdateConversationRequest, UpdateConversationRequestValidator>(services);
        AddValidator<MoveConversationRequest, MoveConversationRequestValidator>(services);
        AddValidator<CreateProjectRequest, CreateProjectRequestValidator>(services);
        AddValidator<UpdateProjectRequest, UpdateProjectRequestValidator>(services);
        AddValidator<CreateMemoryRequest, CreateMemoryRequestValidator>(services);
        AddValidator<UpdateSettingsRequest, UpdateSettingsRequestValidator>(services);
        AddValidator<DocumentUpload, DocumentUploadValidator>(services);
        AddValidator<AnswerRequest, AnswerRequestValidator>(services);
    }

    /// <summary>
    /// Register <typeparamref name="TValidator"/> as both its concrete type and
    /// <c>IValidator&lt;TModel&gt;</c>, sharing one instance.
    /// </summary>
    private static void AddValidator<TModel, TValidator>(IServiceCollection services)
        where TValidator : class, IValidator<TModel>
    {
        services.TryAddSingleton<TValidator>();
        services.TryAddSingleton<IValidator<TModel>>(sp => sp.GetRequiredService<TValidator>());
    }
}
