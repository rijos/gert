namespace Gert.External.Search;

/// <summary>
/// Thrown when the SSRF guard (security F5) refuses a fetch - a non-http(s) scheme, a
/// disallowed URL, a redirect into a blocked target, or a resolved private/loopback/
/// link-local/ULA/metadata address. The web-search adapter catches this and simply
/// drops that result (no snippet), never surfacing it as a server error.
/// </summary>
public sealed class SsrfBlockedException : Exception
{
    /// <summary>Create with a reason describing why the fetch was refused.</summary>
    public SsrfBlockedException(string message)
        : base(message)
    {
    }

    /// <summary>Create with a reason and inner cause.</summary>
    public SsrfBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Parameterless ctor for completeness (analyzers).</summary>
    public SsrfBlockedException()
    {
    }
}
