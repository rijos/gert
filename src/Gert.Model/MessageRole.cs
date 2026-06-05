namespace Gert.Model;

/// <summary>
/// Role of a chat message — mirrors <c>chat.db</c> <c>messages.role</c>
/// (storage-and-data.md § chat.db).
/// </summary>
public enum MessageRole
{
    User,
    Assistant,
    System,
    Tool,
}
