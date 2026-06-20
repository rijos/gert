namespace Gert.Agent;

/// <summary>
/// Phase 2 of a turn, in a worker scope (chat-and-tools.md section detached turns):
/// drive the tool loop against the model, persisting each event to the durable
/// log and publishing it to the bus as it happens - generation is independent
/// of any client connection. Never throws for turn-level failures: a fault
/// finalises the assistant row as <c>error</c> and emits an <c>error</c> event.
/// </summary>
public interface ITurnRunner
{
    Task RunAsync(TurnJob job, CancellationToken cancellationToken = default);
}
