namespace Gert.Model.Rag;

/// <summary>
/// User-level memory-write mode - a user setting in <c>user.db</c> (configuration.md section 2.3):
/// whether the assistant may author memory entries itself.
/// </summary>
public enum MemoryMode
{
    Off,
    Manual,
    Auto,
}
