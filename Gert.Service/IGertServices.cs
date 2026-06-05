using Gert.Service.Account;
using Gert.Service.Admin;
using Gert.Service.Conversations;
using Gert.Service.Documents;
using Gert.Service.Projects;

namespace Gert.Service;

/// <summary>
/// Aggregate hub exposing every granular service as a property (tech-stack.md
/// § Architecture). Controllers inject the one service they need; the Console
/// and cross-service orchestration lean on this hub. Chat is NOT here: the
/// detached turn pipeline (chat-and-tools.md § detached turns) splits it into
/// <see cref="Chat.ITurnPlanner"/> / <see cref="Chat.ITurnQueue"/> /
/// <see cref="Chat.IConversationReader"/> / <see cref="Chat.IConversationStreamer"/>,
/// injected directly where needed.
/// </summary>
public interface IGertServices
{
    IConversationService Conversations { get; }

    IDocumentService Documents { get; }

    IArtifactService Artifacts { get; }

    IMemoryService Memory { get; }

    IProjectService Projects { get; }

    ISettingsService Settings { get; }

    IAccountService Account { get; }

    IAdminService Admin { get; }
}
