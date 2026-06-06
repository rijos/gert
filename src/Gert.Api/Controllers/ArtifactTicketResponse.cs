namespace Gert.Api.Controllers;

/// <summary>The signed render URL for an HTML artifact on the sandbox origin (F3).</summary>
public sealed record ArtifactTicketResponse(string Url);
