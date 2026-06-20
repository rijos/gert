namespace Gert.Tools;

/// <summary>
/// Arguments for the save-memory tool (<c>save_memory</c>): a short
/// <see cref="Title"/> and the <see cref="Content"/> to remember. The tool maps
/// these onto a <c>CreateMemoryRequest</c> and re-proves THAT through the service
/// path (the authoritative caps), so this validator is presence-only - it must not
/// impose conflicting length bounds.
/// </summary>
public sealed record SaveMemoryArgs
{
    /// <summary>A short label for the memory (required).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The fact or preference to remember (required).</summary>
    public string Content { get; init; } = string.Empty;
}
