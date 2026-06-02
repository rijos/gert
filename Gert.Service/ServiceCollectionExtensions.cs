using Gert.Service.Account;
using Gert.Service.Admin;
using Gert.Service.Chat;
using Gert.Service.Conversations;
using Gert.Service.Documents;
using Gert.Service.Projects;
using Gert.Service.Validation;
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

        // Validation seam — passthrough for now (// TODO U6: fail-closed FluentValidation provider).
        services.TryAddScoped<IValidationProvider, PassthroughValidationProvider>();

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

        // Aggregate hub.
        services.TryAddScoped<IGertServices, GertServices>();

        return services;
    }
}
