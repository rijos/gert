namespace Gert.Service.Chat.Bus;

/// <summary>
/// The bus key for one conversation's live event stream. Carries the full
/// identity scope - conversation ids are client-generated UUIDs, so the id alone
/// is not unique across users/projects; scoping the topic the same way the
/// database path is scoped (iss/sub/pid) makes cross-tenant delivery
/// structurally impossible (configuration.md section 2.5).
/// </summary>
public readonly record struct ConversationTopic(
    string Iss,
    string Sub,
    string Pid,
    string ConversationId);
