using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.External.Search;

/// <summary>
/// The SSRF-guarded server-side fetcher (security F5) used by the web-search summarize
/// step to pull a result page. It is the <b>enforcement point</b> for
/// <see cref="SsrfGuard"/>:
///
/// <list type="bullet">
///   <item>Scheme/shape vetting on the initial URL and on <b>every redirect target</b>
///   (redirects are followed manually so each hop is re-checked).</item>
///   <item>Destination-IP vetting at <b>connect time</b> via a
///   <c>SocketsHttpHandler.ConnectCallback</c>: the host is resolved there and each
///   candidate address is run through <see cref="SsrfGuard.IsIpAllowed"/> before a
///   socket is opened — so a DNS name that resolves to a private IP (or a DNS-rebind)
///   is blocked at the TCP layer, not just at URL-parse time.</item>
///   <item>Caps on response size, wall-clock time, and redirect count.</item>
/// </list>
///
/// <para>
/// The <c>ConnectCallback</c> + redirect re-check are the parts that need a live socket
/// to fully exercise; <see cref="SsrfGuard"/> itself is unit-tested directly. A blocked
/// destination throws <see cref="SsrfBlockedException"/>, which the caller treats as
/// "skip this result", never as a server error.
/// </para>
/// </summary>
public sealed class SafeHttpFetcher : IDisposable
{
    private readonly HttpClient _client;
    private readonly SearXngOptions _options;
    private readonly ILogger<SafeHttpFetcher> _logger;

    /// <summary>Construct a fetcher whose handler enforces the connect-time IP guard.</summary>
    public SafeHttpFetcher(IOptions<SearXngOptions> options, ILogger<SafeHttpFetcher> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var handler = new SocketsHttpHandler
        {
            // We follow redirects manually so each hop is re-vetted (F5).
            AllowAutoRedirect = false,
            // The connect-time IP guard: resolve the host here and refuse a socket to
            // any blocked address. This is the DNS-rebind-proof layer.
            ConnectCallback = GuardedConnectAsync,
            AutomaticDecompression = DecompressionMethods.All,
        };

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(_options.FetchTimeoutSeconds),
        };
    }

    /// <summary>
    /// Fetch a URL with the full SSRF policy applied. Returns the decoded body (capped),
    /// or throws <see cref="SsrfBlockedException"/> if the URL / any redirect / any
    /// resolved IP is disallowed.
    /// </summary>
    public async Task<string> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var current))
        {
            throw new SsrfBlockedException($"Malformed URL '{url}'.");
        }

        for (var hop = 0; hop <= _options.MaxRedirects; hop++)
        {
            if (!SsrfGuard.IsUrlAllowed(current))
            {
                throw new SsrfBlockedException($"URL blocked by SSRF policy: {current}");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location is null)
                {
                    throw new SsrfBlockedException("Redirect without Location header.");
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue; // re-vet on the next loop iteration (F5: re-check after each redirect)
            }

            response.EnsureSuccessStatusCode();
            return await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
        }

        throw new SsrfBlockedException("Too many redirects.");
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// The connect-time guard: resolve the host, refuse any blocked address, and open a
    /// socket only to an allowed one. This runs for every connection, including those
    /// established after a redirect.
    /// </summary>
    private async ValueTask<Stream> GuardedConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        foreach (var address in addresses)
        {
            if (!SsrfGuard.IsIpAllowed(address))
            {
                _logger.LogWarning("SSRF guard blocked connect to {Host} → {Ip}.", host, address);
                throw new SsrfBlockedException($"Resolved address blocked by SSRF policy: {address}");
            }
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[8192];
        using var accumulated = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (accumulated.Length + read > _options.MaxFetchBytes)
            {
                _logger.LogWarning("SSRF fetch exceeded size cap of {Cap} bytes; truncating.", _options.MaxFetchBytes);
                var remaining = (int)(_options.MaxFetchBytes - accumulated.Length);
                if (remaining > 0)
                {
                    accumulated.Write(buffer, 0, remaining);
                }

                break;
            }

            accumulated.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(accumulated.ToArray());
    }
}
