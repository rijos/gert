using FluentValidation;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Gert.Service.Account;
using Gert.Service.Admin;
using Gert.Service.Chat;
using Gert.Service.Conversations;
using Gert.Service.Documents;
using Gert.Service.Projects;
using Gert.Service.Tools;
using Gert.Service.Validation;
using Gert.Service.Validation.Validators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.Service;

/// <summary>
/// DI wiring for the host-agnostic service layer (tech-stack.md § Architecture).
/// Registers the granular services, the aggregate <see cref="IGertServices"/>
/// hub, and the validation seam. The host/adapters supply the ports the services
/// depend on — <see cref="IUserContext"/> (auth host),
/// <see cref="Database.IDatabaseProvider"/> (a database adapter), and
/// <see cref="External.IChatModelClient"/> (Gert.External) — so this method does
/// not register them.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Gert service layer. Services are <b>scoped</b> — they depend
    /// on the per-request <see cref="IUserContext"/>, so their lifetime must not
    /// outlive a request. Uses <c>TryAdd</c> so a host may override any registration.
    /// </summary>
    public static IServiceCollection AddGertServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Validation seam (U6) — the fail-closed FluentValidation-backed provider
        // plus a validator for every request DTO a service accepts. A DTO with no
        // registered IValidator<T> makes the provider throw (principle #6); the
        // reflection meta-test keeps that throw unreachable in production.
        AddValidation(services);

        // Step-0 instructions reader — default to "no instructions" so the service
        // layer is self-contained; a host that can read project meta.json overrides it.
        services.TryAddScoped<IProjectInstructionsReader, NullProjectInstructionsReader>();

        // Granular services.
        services.TryAddScoped<IChatService, ChatService>();
        services.TryAddScoped<IConversationService, ConversationService>();
        services.TryAddScoped<IDocumentService, DocumentService>();
        services.TryAddScoped<IArtifactService, ArtifactService>();
        services.TryAddScoped<IMemoryService, MemoryService>();
        services.TryAddScoped<IProjectService, ProjectService>();
        services.TryAddScoped<ISettingsService, SettingsService>();
        services.TryAddScoped<IAccountService, AccountService>();
        services.TryAddScoped<IAdminService, AdminService>();

        // Tools (U7c) — each ITool is registered so the ToolRegistry is populated
        // from IEnumerable<ITool>. They are scoped: RagTool depends on the
        // per-request IUserContext, so a tool's lifetime must not outlive a request.
        AddTools(services);

        // Aggregate hub.
        services.TryAddScoped<IGertServices, GertServices>();

        return services;
    }

    /// <summary>
    /// The canonical capability ids of the built-in tools (U7c) — the id-only
    /// <see cref="ToolRegistry"/> singleton is built from these, matching the
    /// <see cref="ITool.Id"/> of each registered tool. Keep in sync with
    /// <see cref="AddTools"/>.
    /// </summary>
    private static readonly string[] BuiltInToolIds = ["rag", "search", "sandbox"];

    /// <summary>
    /// Register the built-in tools (U7c) as scoped <see cref="ITool"/>s so the
    /// orchestrator resolves them via <c>IEnumerable&lt;ITool&gt;</c>. Scoped
    /// because <see cref="Tools.RagTool"/> depends on the per-request
    /// <see cref="IUserContext"/>. The external ports each tool needs
    /// (<see cref="External.IEmbeddingClient"/>, <see cref="External.IWebSearch"/>,
    /// <see cref="External.ISandbox"/>) and the <see cref="Database.IDatabaseProvider"/>
    /// are supplied by the host/adapters (Gert.External / a database adapter).
    /// </summary>
    private static void AddTools(IServiceCollection services)
    {
        services.AddScoped<ITool, RagTool>();
        services.AddScoped<ITool, WebSearchTool>();
        services.AddScoped<ITool, SandboxTool>();
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
        // singleton built from the registered tools' canonical ids — it never holds
        // the per-request scoped tool instances (which the orchestrator resolves via
        // IEnumerable<ITool>). Registered as a singleton so the singleton validators
        // that take it are not captive-dependent on a scoped service.
        services.TryAddSingleton(new ToolRegistry(BuiltInToolIds));

        // Shared / nested validators — needed by concrete type for SetValidator.
        AddValidator<ToolToggles, ToolTogglesValidator>(services);
        AddValidator<GenerationParams, GenerationParamsValidator>(services);
        AddValidator<ProjectDefaults, ProjectDefaultsValidator>(services);

        // Request DTOs every service method accepts.
        AddValidator<SendMessageRequest, SendMessageRequestValidator>(services);
        AddValidator<CreateConversationRequest, CreateConversationRequestValidator>(services);
        AddValidator<UpdateConversationRequest, UpdateConversationRequestValidator>(services);
        AddValidator<CreateProjectRequest, CreateProjectRequestValidator>(services);
        AddValidator<UpdateProjectRequest, UpdateProjectRequestValidator>(services);
        AddValidator<CreateMemoryRequest, CreateMemoryRequestValidator>(services);
        AddValidator<UpdateSettingsRequest, UpdateSettingsRequestValidator>(services);
        AddValidator<DocumentUpload, DocumentUploadValidator>(services);
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
