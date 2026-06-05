namespace Gert.Model;

/// <summary>
/// User-level memory-write mode — mirrors <c>settings.json</c> (configuration.md § 2.3):
/// whether the assistant may author memory entries itself.
/// </summary>
public enum MemoryMode
{
    Off,
    Manual,
    Auto,
}
