using System.Net;

namespace Gert.Tools.Tests;

/// <summary>
/// A test <see cref="HttpMessageHandler"/> that captures the outgoing request and
/// returns a canned response - lets the vLLM client be driven without a server. The
/// captured <see cref="LastRequestBody"/> is asserted for request shaping; the
/// response is supplied by the test (an SSE stream or an embeddings JSON body).
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    /// <summary>Build over a responder that sees the request + its captured body.</summary>
    public StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
    {
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
    }

    /// <summary>The body of the most recent request, as a string.</summary>
    public string? LastRequestBody { get; private set; }

    /// <summary>The most recent request URI.</summary>
    public Uri? LastRequestUri { get; private set; }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return _responder(request, LastRequestBody ?? string.Empty);
    }
}
