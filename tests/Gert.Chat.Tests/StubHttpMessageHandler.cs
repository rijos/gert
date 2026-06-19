using System.Net;

namespace Gert.Chat.Tests;

/// <summary>
/// Test <see cref="HttpMessageHandler"/> that captures the outgoing request and returns a
/// canned response, driving the chat client without a server. <see cref="LastRequestBody"/>
/// is asserted for request shaping; the response comes from the test's responder.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
    {
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
    }

    public string? LastRequestBody { get; private set; }

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
