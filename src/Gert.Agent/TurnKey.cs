using Gert.Service;

namespace Gert.Agent;

/// <summary>
/// Identity of one in-flight turn in the cancellation registry: the full tenant
/// tuple, not the bare conversation id. The 409 rule allows at most one turn per
/// conversation, so conversation granularity is exact; carrying iss/sub/pid means
/// a caller can only ever address turns in their own tenant (the cancel endpoint
/// builds the key from the authenticated <see cref="IUserContext"/>).
/// </summary>
public readonly record struct TurnKey(string Iss, string Sub, string Pid, string ConversationId)
{
    public static TurnKey From(TurnJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new TurnKey(job.Iss, job.Sub, job.Pid, job.ConversationId);
    }
}
