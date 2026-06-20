namespace Gert.Tools;

/// <summary>
/// Arguments for the canvas list tool (<c>list_artifacts</c>): none - it lists every
/// artifact in the conversation. The empty record still needs a registered validator
/// (the typed-args base proves args fail-closed before <c>CallAsync</c>).
/// </summary>
public sealed record ListArtifactsArgs;
