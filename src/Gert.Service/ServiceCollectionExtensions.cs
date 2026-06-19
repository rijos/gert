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
/// (<see cref="Database.IUserDatabaseProvider"/> / <see cref="Database.IChatDatabaseProvider"/>),
/// the RAG index provider (<see cref="global::Gert.Rag.IRagIndexProvider"/>), and
/// <see cref="External.IChatModelClient"/> (an adapter assembly) - so this method does
/// not register them.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Gert service layer. Most services are registered with
    /// <see cref="AddUserScoped{TService,TImpl}"/> - they act on behalf of the ambient
    /// caller, resolving identity from the per-request <see cref="IUserContext"/> and
    /// opening the per-user store by <c>(Iss, Sub)</c>. <b>Which</b> impl backs
    /// <see cref="IUserContext"/> is chosen by the host: a request scope gets
    /// <c>HttpUserContext</c> (JWT claims); a worker scope (no <c>HttpContext</c>) gets
    /// <see cref="DetachedUserContext"/> seeded from the job - see the "IUserContext
    /// routing" registration in <c>Gert.Api/Program.cs</c>. Uses <c>TryAdd</c> so a host
    /// may override any registration.
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

        // Granular services. All caller-bound (see AddUserScoped): they read/write the
        // requester's per-user store via IUserContext, so they must never outlive a scope.
        services.AddUserScoped<IConversationService, ConversationService>();

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
        services.AddUserScoped<IConversationReader, ConversationReader>();
        services.AddUserScoped<IConversationStreamer, ConversationStreamer>();
        services.AddUserScoped<ITurnPlanner, TurnPlanner>();
        services.AddUserScoped<ITurnRunner, TurnRunner>();
        // The worker-scope IUserContext: seeded from the TurnJob before anything
        // else resolves in the scope. The host's IUserContext registration picks
        // this when there is no HttpContext (the queue seam: ITurnQueue is
        // host-registered, like IIngestionQueue).
        services.TryAddScoped<DetachedUserContext>();
        // TurnOptions get DEFAULTS here; the host binds "Gert:Turn" over them
        // (this layer stays configuration-agnostic).
        services.AddOptions<TurnOptions>();

        services.AddUserScoped<IDocumentService, DocumentService>();
        services.AddUserScoped<IArtifactService, ArtifactService>();
        services.AddUserScoped<IMemoryService, MemoryService>();
        services.AddUserScoped<IProjectService, ProjectService>();
        services.AddUserScoped<ISettingsService, SettingsService>();
        // The journal-guarded eraser: one crash-consistent erase path for self-service
        // account delete, admin delete, and recovery. Singleton - its stores + journal are
        // all singletons, so the startup recovery sweep can depend on it directly.
        services.TryAddSingleton<IUserDataEraser, UserDataEraser>();
        services.AddUserScoped<IAccountService, AccountService>();
        services.AddUserScoped<IAdminService, AdminService>();
        services.AddUserScoped<ISystemPromptInspector, SystemPromptInspector>();
        services.AddUserScoped<Provisioning.IUserProvisioner, Provisioning.UserProvisioner>();

        // Ingestion - the extract -> chunk -> embed -> write pipeline and its
        // ports. Files are read/written ONLY via IObjectStore (host-registered) and
        // text-extraction is behind ITextExtractor so Gert.Ingestion swaps the hardened pdf/docx
        // extractor in with one registration. The default queue runs ingestion inline;
        // The API host replaces it with a Channel-backed queue + BackgroundService (responds 202).
        AddIngestion(services);

        // The built-in tool IMPLEMENTATIONS live in the Gert.Tools adapter (AddBuiltinTools,
        // called by the host's AddGertTools). This layer keeps only the id-only ToolRegistry +
        // BuiltInToolIds census (registered in AddValidation): they name capability ids, not
        // impls. The orchestrator resolves the tool instances via IEnumerable<ITool>.

        services.AddUserScoped<IGertServices, GertServices>();

        return services;
    }

    /// <summary>
    /// Register a service that <b>acts on behalf of the ambient caller</b>: it resolves
    /// the user from the request-scoped <see cref="IUserContext"/> (directly or through a
    /// collaborator) and opens the per-user store by <c>(Iss, Sub)</c>. Such a service
    /// <b>MUST be scoped</b> - a singleton would capture one caller's identity once and
    /// then serve their data to every later request (a captive-dependency cross-user
    /// leak, not just a lifetime bug). This helper is plain <c>TryAddScoped</c>; its name
    /// is the contract. The "must be scoped" half is enforced two ways: the host enables
    /// <c>ValidateScopes</c>/<c>ValidateOnBuild</c> (Program.cs), and
    /// <c>ArchitectureTests.Services_consuming_IUserContext_are_scoped</c> fails if any
    /// registration here turns singleton. Use <c>TryAddScoped</c> directly only for the
    /// rare scoped service that is <i>not</i> caller-bound (e.g. a stateless seam or the
    /// <see cref="DetachedUserContext"/> impl itself).
    /// </summary>
    private static IServiceCollection AddUserScoped<TService, TImpl>(this IServiceCollection services)
        where TService : class
        where TImpl : class, TService
    {
        services.TryAddScoped<TService, TImpl>();
        return services;
    }

    /// <summary>
    /// The canonical capability ids of the built-in tools -- the id-only
    /// <see cref="ToolRegistry"/> singleton is built from these, matching the
    /// <see cref="ITool.Id"/> of each registered tool. Keep in sync with the
    /// <c>AddBuiltinTools</c> registrations in the Gert.Tools adapter; the
    /// <c>ToolRegistrationTests</c> set-equality guard fails if they drift.
    /// </summary>
    private static readonly string[] BuiltInToolIds =
        ["rag", "search", "sandbox", "todo", "clock", "make_artifact", "edit_artifact", "read_artifact", "ask_user", "fetch", "memory", "sub_agent"];

    /// <summary>
    /// DI key for the per-type leaf <see cref="ITextExtractor"/>s the
    /// <see cref="CompositeTextExtractor"/> composes. Keying them keeps the composite
    /// (registered as the plain <see cref="ITextExtractor"/>) out of its own
    /// enumeration. The pdf/docx extractor (Gert.Ingestion) registers under the same
    /// key, so it is <c>public</c> for that adapter to reference.
    /// </summary>
    public const string LeafExtractorKey = "leaf";

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

        // The per-type leaf extractors live in the Gert.Ingestion adapter; AddGertIngestion
        // registers them under the "leaf" key so the composite (registered as the plain
        // ITextExtractor the pipeline depends on) can enumerate them without re-entering its
        // own resolution. This layer wires only the composite + the pipeline.
        services.TryAddSingleton<ITextExtractor>(sp =>
            new CompositeTextExtractor(sp.GetKeyedServices<ITextExtractor>(LeafExtractorKey)));

        services.AddUserScoped<IIngestionService, IngestionService>();
        // The inline queue is infrastructure, not caller-bound: it just runs the pipeline
        // on the calling scope (the API host swaps it for a Channel-backed singleton).
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
