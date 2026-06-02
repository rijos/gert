namespace Gert.Model.Dtos;

/// <summary>
/// Per-conversation / per-request tool preferences — the <c>tools_json</c>
/// shape <c>{"rag":true,"search":true,"sandbox":false}</c> (rest-api.md
/// § sending a message; storage-and-data.md § chat.db). These are *preferences*;
/// the JWT entitlement is the hard ceiling (auth.md § entitlement).
/// </summary>
public sealed record ToolToggles
{
    public bool Rag { get; init; }

    public bool Search { get; init; }

    public bool Sandbox { get; init; }
}
