using Gert.Model.Dtos;
using Gert.Service.Chat;
using Gert.Validation;

namespace Gert.Agent;

/// <summary>
/// Phase 1 of a turn, in the request scope (chat-and-tools.md section detached turns):
/// validate, materialize the conversation if new, reject a concurrent turn
/// (409), persist the user message and the <c>streaming</c> assistant row, and
/// capture everything the off-thread runner needs as a <see cref="TurnJob"/>.
/// Throws <see cref="Validation.ValidationException"/> (-> 400) or
/// <see cref="TurnInProgressException"/> (-> 409) before any enqueue.
/// </summary>
public interface ITurnPlanner
{
    Task<TurnJob> PlanAsync(
        string pid,
        string conversationId,
        Validated<SendMessageRequest> request,
        CancellationToken cancellationToken = default);
}
