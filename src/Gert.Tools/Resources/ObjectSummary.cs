namespace Gert.Tools;

/// <summary>An object's metadata without its content - the shape <see cref="IObjectResource.ListAsync"/> returns.</summary>
public sealed record ObjectSummary
{
    /// <summary>The object's stable id.</summary>
    public required string Id { get; init; }

    /// <summary>The object's name.</summary>
    public required string Name { get; init; }

    /// <summary>The current version.</summary>
    public required int Version { get; init; }

    /// <summary>The object's kind tag.</summary>
    public required string Kind { get; init; }

    /// <summary>When the object was first created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the object was last written.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
