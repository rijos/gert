using Gert.Service.Account;
using Gert.Service.Admin;
using Gert.Service.Conversations;
using Gert.Service.Documents;
using Gert.Service.Projects;

namespace Gert.Service;

/// <summary>
/// Aggregate hub exposing every granular service as a property
/// (tech-stack.md § Architecture). The granular services are ctor-injected and
/// surfaced unchanged — the hub is composition, not behaviour.
/// </summary>
public sealed class GertServices : IGertServices
{
    public GertServices(
        IConversationService conversations,
        IDocumentService documents,
        IArtifactService artifacts,
        IMemoryService memory,
        IProjectService projects,
        ISettingsService settings,
        IAccountService account,
        IAdminService admin)
    {
        Conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        Documents = documents ?? throw new ArgumentNullException(nameof(documents));
        Artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Projects = projects ?? throw new ArgumentNullException(nameof(projects));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Account = account ?? throw new ArgumentNullException(nameof(account));
        Admin = admin ?? throw new ArgumentNullException(nameof(admin));
    }

    /// <inheritdoc />
    public IConversationService Conversations { get; }

    /// <inheritdoc />
    public IDocumentService Documents { get; }

    /// <inheritdoc />
    public IArtifactService Artifacts { get; }

    /// <inheritdoc />
    public IMemoryService Memory { get; }

    /// <inheritdoc />
    public IProjectService Projects { get; }

    /// <inheritdoc />
    public ISettingsService Settings { get; }

    /// <inheritdoc />
    public IAccountService Account { get; }

    /// <inheritdoc />
    public IAdminService Admin { get; }
}
