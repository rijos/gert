namespace Gert.Model.Chat;

/// <summary>
/// Lifecycle of a message row (storage-and-data.md § chat.db). User messages are
/// born <see cref="Complete"/>; an assistant row is inserted as
/// <see cref="Streaming"/> when its turn is planned, then finalised to
/// <see cref="Complete"/>, <see cref="Error"/>, or <see cref="Cancelled"/> by
/// the turn runner. Readers must apply the orphan rule: a
/// <see cref="Streaming"/> row older than the max turn duration is reported as
/// <see cref="Error"/> (the in-memory turn queue is not durable — a crashed
/// worker never finalises its row).
/// </summary>
public enum MessageStatus
{
    /// <summary>The turn is (presumed) in flight; content is partial.</summary>
    Streaming,

    /// <summary>The turn finished; content and token count are final.</summary>
    Complete,

    /// <summary>The turn faulted; content holds whatever streamed before the fault.</summary>
    Error,

    /// <summary>The user stopped the turn; content holds whatever streamed before the stop.</summary>
    Cancelled,
}
