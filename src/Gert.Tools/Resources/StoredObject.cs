namespace Gert.Tools;

/// <summary>A stored object with its content and metadata (the result of a get or a put).</summary>
public sealed record StoredObject
{
    /// <summary>The object's name - the handle a tool reads/writes by (e.g. <c>decision.md</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The object's full content.</summary>
    public required string Content { get; init; }

    /// <summary>The monotonically increasing version, bumped on every overwrite.</summary>
    public required int Version { get; init; }

    /// <summary>The object's kind tag (e.g. <c>markdown</c>, <c>memory</c>) - tool-defined.</summary>
    public required string Kind { get; init; }

    /// <summary>When the object was first created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the object was last written.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
