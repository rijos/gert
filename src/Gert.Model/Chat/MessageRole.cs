namespace Gert.Model.Chat;

/// <summary>
/// Role of a chat message - mirrors <c>chat.db</c> <c>messages.role</c>
/// (storage-and-data.md section chat.db).
/// </summary>
public enum MessageRole
{
    User,
    Assistant,
    System,
    Tool,
}
