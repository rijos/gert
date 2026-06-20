namespace Gert.Tools.Search.SearXNG;

/// <summary>
/// The SearXNG connection (the <c>Parameters</c> bag of <see cref="SearXngOptions"/>, bound
/// from <c>Gert:Tools:Search:Parameters</c>) - what changes when the search
/// <see cref="SearXngOptions.Type"/> changes. All non-secret.
/// </summary>
public sealed class SearXngParameters
{
    /// <summary>Base URL of the SearXNG instance, e.g. <c>http://searxng:8080</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";
}
