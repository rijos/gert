using Gert.Model.Dtos;
using Gert.Model.Events;
using Gert.Service;
using Gert.Service.Chat;
using Gert.Service.External;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Console.Tui.State;

/// <summary>
/// Drives one conversation's turns for the TUI (U16): the same inline
/// plan → run → stream pipeline as <see cref="ConsoleApp"/>'s chat command,
/// but pushed through a UI marshal into the <see cref="ChatTranscript"/>.
/// Headless by construction — the marshal is an injected delegate
/// (<c>Application.Invoke</c> in the TUI, run-inline in tests), so no
/// Terminal.Gui types appear here.
/// </summary>
public sealed class ChatPresenter
{
    private const string DefaultProject = "default";

    private readonly IServiceProvider _services;
    private readonly Action<Action> _uiInvoke;
    private TurnJob? _activeJob;

    public ChatPresenter(IServiceProvider services, ChatTranscript transcript, Action<Action>? uiInvoke = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _uiInvoke = uiInvoke ?? (action => action());
    }

    /// <summary>The transcript this presenter feeds.</summary>
    public ChatTranscript Transcript { get; }

    /// <summary>The active project.</summary>
    public string Pid { get; set; } = DefaultProject;

    /// <summary>The active conversation; null = new chat (created on first send).</summary>
    public string? ConversationId { get; private set; }

    /// <summary>Raised (on the UI thread) when a send created a new conversation.</summary>
    public event Action<string>? ConversationCreated;

    /// <summary>True while a turn is in flight.</summary>
    public bool IsStreaming => Transcript.Streaming;

    /// <summary>Switch to an existing conversation (the transcript is rebuilt by the caller).</summary>
    public void Attach(string? conversationId) => ConversationId = conversationId;

    /// <summary>New chat: clear the thread; the next send creates the conversation.</summary>
    public void NewConversation()
    {
        ConversationId = null;
        _activeJob = null;
        Transcript.Clear();
    }

    /// <summary>
    /// Send one user message and stream the turn to completion. The returned
    /// task completes when the turn ends (the TUI fires-and-forgets it; tests
    /// await it).
    /// </summary>
    public async Task SendAsync(string content, ComposerState composer, CancellationToken cancellationToken = default)
    {
        // Whitespace is NOT rejected here: the service-layer validator rejects it
        // (testing.md §7 — identical on the console path), surfacing as an error
        // entry below.
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(composer);

        _uiInvoke(() =>
        {
            Transcript.AddUser(content);
            Transcript.BeginAssistant();
        });

        try
        {
            await RunTurnAsync(content, composer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Plan-time rejections (validation, turn-in-progress) and any other
            // fault surface in the transcript — the same message the API's 400
            // body would carry.
            _uiInvoke(() => Transcript.Apply(new ErrorEvent { Message = ex.Message }));
        }
    }

    /// <summary>
    /// Re-attach to an in-flight turn after a conversation switch (the thread's
    /// last assistant row is still <c>streaming</c>): stream from
    /// <paramref name="afterSeq"/> without planning or running anything.
    /// </summary>
    public async Task ResumeAsync(long afterSeq, CancellationToken cancellationToken = default)
    {
        if (ConversationId is not { } conversationId)
        {
            return;
        }

        await using var scope = _services.CreateAsyncScope();
        var streamer = scope.ServiceProvider.GetRequiredService<IConversationStreamer>();
        await ConsumeAsync(streamer, conversationId, afterSeq, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stop the in-flight turn through the runner's user-cancel registry — NOT
    /// by cancelling our stream token (that would finalize as <c>error</c>;
    /// the registry path finalizes as <c>cancelled</c>).
    /// </summary>
    public bool Stop()
    {
        if (_activeJob is not { } job)
        {
            return false;
        }

        var cancellation = _services.GetRequiredService<ITurnCancellation>();
        return cancellation.Cancel(TurnKey.From(job));
    }

    private async Task RunTurnAsync(string content, ComposerState composer, CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        if (ConversationId is null)
        {
            var conversation = await provider.GetRequiredService<IGertServices>().Conversations
                .CreateAsync(Pid, new CreateConversationRequest { ModelId = composer.ModelId }, cancellationToken)
                .ConfigureAwait(false);
            ConversationId = conversation.Id;
            _uiInvoke(() => ConversationCreated?.Invoke(conversation.Id));
        }

        var request = new SendMessageRequest
        {
            Content = content,
            ModelId = composer.ModelId,
            Tools = composer.ToToolToggles(),
            Thinking = composer.Thinking,
            PreserveThinking = composer.PreserveThinking,
        };

        var planner = provider.GetRequiredService<ITurnPlanner>();
        var runner = provider.GetRequiredService<ITurnRunner>();
        var streamer = provider.GetRequiredService<IConversationStreamer>();

        var job = await planner.PlanAsync(Pid, ConversationId, request, cancellationToken).ConfigureAwait(false);
        _activeJob = job;

        // The model the planner resolved decides the context-window capacity.
        var catalog = provider.GetRequiredService<IModelCatalog>();
        var capacity = catalog.List().FirstOrDefault(m => string.Equals(m.Id, job.ModelId, StringComparison.Ordinal))?.Context;
        _uiInvoke(() => Transcript.ContextCapacity = capacity);

        var run = runner.RunAsync(job, cancellationToken);
        try
        {
            await ConsumeAsync(streamer, job.ConversationId, job.AssistantSeq, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await run.ConfigureAwait(false);
            _activeJob = null;
        }
    }

    private async Task ConsumeAsync(
        IConversationStreamer streamer,
        string conversationId,
        long afterSeq,
        CancellationToken cancellationToken)
    {
        await foreach (var turnEvent in streamer
            .StreamAsync(Pid, conversationId, afterSeq, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            var chatEvent = turnEvent.Event;
            _uiInvoke(() => Transcript.Apply(chatEvent));

            // The turn's slice of the endless conversation stream — note that
            // unlike ConsoleApp's CLI loop, a TUI stop must ALSO terminate
            // (cancelled is a terminal event here).
            if (chatEvent is MessageEndEvent or ErrorEvent or CancelledEvent)
            {
                break;
            }
        }
    }
}
