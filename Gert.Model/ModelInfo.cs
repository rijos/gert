namespace Gert.Model;

/// <summary>
/// One entry in the model catalog (rest-api.md § models) — the operator-configured
/// list behind <c>GET /api/models</c> and the capability gate for tool calling.
/// Only id + name are required; config binding constructs an entry from whichever
/// keys the operator sets.
/// </summary>
public sealed record ModelInfo
{
    /// <summary>The capability token that marks a model as tool-calling capable.</summary>
    public const string ToolsCapability = "tools";

    /// <summary>Model id sent upstream as <c>model</c> (or the literal <c>default</c>).</summary>
    public string Id { get; init; } = "";

    /// <summary>Display name for the picker.</summary>
    public string Name { get; init; } = "";

    /// <summary>The catalog's flagged default (the server level of the config cascade).</summary>
    public bool Default { get; init; }

    /// <summary>
    /// Capability tokens (<c>tools</c>, <c>vision</c>, …). <c>null</c> = undeclared:
    /// the model is assumed tool-capable rather than silently crippled.
    /// </summary>
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>Context window in tokens (badge: "128K ctx"). Null = unknown.</summary>
    public int? Context { get; init; }

    public bool Fast { get; init; }

    /// <summary>Display-only endpoint hint, e.g. <c>:8001</c>.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Tool-calling capable — declared, or undeclared (permissive).</summary>
    public bool SupportsTools => Capabilities is null || Capabilities.Contains(ToolsCapability);
}
