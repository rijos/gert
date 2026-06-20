namespace Gert.Tools.Resources;

/// <summary>The input to <see cref="IObjectResource.PutAsync"/>: the name, content, and kind to store.</summary>
public sealed record ObjectWrite
{
    /// <summary>The object's name (the create-or-overwrite key within the scope).</summary>
    public required string Name { get; init; }

    /// <summary>The content to store.</summary>
    public required string Content { get; init; }

    /// <summary>The kind tag to record.</summary>
    public required string Kind { get; init; }
}
