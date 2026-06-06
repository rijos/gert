using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Service;
using Gert.Service.Projects;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Console.Tui.State;

/// <summary>
/// The sidebar's model (U16) — projects + the active project's conversation
/// list, the console analog of <c>convo-list.js</c> + <c>project-picker.js</c>.
/// Headless; async loads marshal their mutations through the injected UI
/// invoke.
/// </summary>
public sealed class SidebarPresenter
{
    private const string DefaultProject = "default";

    private readonly IServiceProvider _services;
    private readonly Action<Action> _uiInvoke;
    private List<ProjectSummary> _projects = [];
    private List<Conversation> _conversations = [];

    public SidebarPresenter(IServiceProvider services, Action<Action>? uiInvoke = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _uiInvoke = uiInvoke ?? (action => action());
    }

    /// <summary>Raised (on the UI thread) when the lists change.</summary>
    public event Action? Changed;

    /// <summary>The user's projects.</summary>
    public IReadOnlyList<ProjectSummary> Projects => _projects;

    /// <summary>The active project's conversations (most recent first).</summary>
    public IReadOnlyList<Conversation> Conversations => _conversations;

    /// <summary>The active project.</summary>
    public string Pid { get; private set; } = DefaultProject;

    /// <summary>The opened conversation (null = new chat).</summary>
    public string? SelectedConversationId { get; set; }

    /// <summary>Reload projects + the active project's conversations.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();

        var projects = await gert.Projects.ListAsync(cancellationToken).ConfigureAwait(false);
        var conversations = await gert.Conversations.ListAsync(Pid, cancellationToken).ConfigureAwait(false);

        _uiInvoke(() =>
        {
            _projects = projects.ToList();
            _conversations = conversations
                .Where(c => !c.Archived)
                .OrderByDescending(c => c.UpdatedAt)
                .ToList();
            Changed?.Invoke();
        });
    }

    /// <summary>Load one conversation's full thread and mark it selected.</summary>
    public async Task<ConversationThread?> OpenAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        await using var scope = _services.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
        var thread = await gert.Conversations.GetAsync(Pid, conversationId, cancellationToken).ConfigureAwait(false);
        if (thread is not null)
        {
            _uiInvoke(() => SelectedConversationId = conversationId);
        }

        return thread;
    }

    /// <summary>Rename a conversation, then refresh.</summary>
    public async Task RenameAsync(string conversationId, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        await using (var scope = _services.CreateAsyncScope())
        {
            var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
            await gert.Conversations
                .UpdateAsync(Pid, conversationId, new UpdateConversationRequest { Title = title }, cancellationToken)
                .ConfigureAwait(false);
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delete a conversation (cascades messages/tools/citations), then refresh.</summary>
    public async Task DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        await using (var scope = _services.CreateAsyncScope())
        {
            var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
            await gert.Conversations.DeleteAsync(Pid, conversationId, cancellationToken).ConfigureAwait(false);
        }

        _uiInvoke(() =>
        {
            if (string.Equals(SelectedConversationId, conversationId, StringComparison.Ordinal))
            {
                SelectedConversationId = null;
            }
        });
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Switch the active project and reload its conversations.</summary>
    public async Task SwitchProjectAsync(string pid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pid);

        _uiInvoke(() =>
        {
            Pid = pid;
            SelectedConversationId = null;
        });
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Create a project and switch into it.</summary>
    public async Task CreateProjectAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string pid;
        await using (var scope = _services.CreateAsyncScope())
        {
            var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();
            var meta = await gert.Projects
                .CreateAsync(new CreateProjectRequest { Name = name }, cancellationToken)
                .ConfigureAwait(false);
            pid = meta.Id;
        }

        await SwitchProjectAsync(pid, cancellationToken).ConfigureAwait(false);
    }
}
