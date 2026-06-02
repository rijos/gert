using Gert.Service.Account;
using Gert.Service.Admin;
using Gert.Service.Chat;
using Gert.Service.Conversations;
using Gert.Service.Documents;
using Gert.Service.Projects;

namespace Gert.Service;

/// <summary>
/// Aggregate hub exposing every granular service as a property (tech-stack.md
/// § Architecture). Controllers inject the one service they need; the Console
/// and cross-service orchestration lean on this hub.
/// </summary>
public interface IGertServices
{
    IChatService Chat { get; }

    IConversationService Conversations { get; }

    IDocumentService Documents { get; }

    IArtifactService Artifacts { get; }

    IMemoryService Memory { get; }

    IProjectService Projects { get; }

    ISettingsService Settings { get; }

    IAccountService Account { get; }

    IAdminService Admin { get; }
}
