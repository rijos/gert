using System.Net;
using System.Net.Sockets;
using System.Text;
using Gert.Tools.Search.SearXNG;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Tools.Fetch;

/// <summary>
/// The SSRF-guarded server-side fetcher (security F5) used by the web-search summarize
/// step to pull a result page. It is the <b>enforcement point</b> for
/// <see cref="SsrfGuard"/>:
///
/// <list type="bullet">
///   <item>Scheme/shape vetting on the initial URL and on <b>every redirect target</b>
///   (redirects are followed manually so each hop is re-checked).</item>
///   <item>Destination scoped to <b>IPv4 + ports 80/443</b>: a non-web port is refused
///   via <see cref="SsrfGuard.IsPortAllowed"/> on every hop, and any non-IPv4 resolved
///   address is dropped at connect time - so the fetch can't be turned into a probe of
///   internal non-web services.</item>
///   <item>Destination-IP vetting at <b>connect time</b> via a
///   <c>SocketsHttpHandler.ConnectCallback</c>: the host is resolved there and each
///   candidate address is run through <see cref="SsrfGuard.IsIpAllowed"/> before a
///   socket is opened - so a DNS name that resolves to a private IP (or a DNS-rebind)
///   is blocked at the TCP layer, not just at URL-parse time.</item>
///   <item>Caps on response size, wall-clock time, and redirect count.</item>
/// </list>
///
/// <para>
/// A blocked destination throws <see cref="SsrfBlockedException"/>, which the caller
/// treats as "skip this result", never as a server error. <see cref="SsrfGuard"/>
/// itself is unit-tested directly; the redirect re-vet and the connect-time DNS pin
/// are pinned by <c>SafeHttpFetcherRedirectTests</c> via the <b>internal</b>
/// constructor's resolver / IP-check delegates - deliberately not a configuration
/// knob, so the production wiring (real DNS + <see cref="SsrfGuard.IsIpAllowed"/>)
/// cannot be bypassed by an operator setting.
/// </para>
/// </summary>
public sealed class SafeHttpFetcher : IDisposable
{
    private readonly HttpClient _client;
    private readonly SearXngOptions _options;
    private readonly ILogger<SafeHttpFetcher> _logger;
    private readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> _resolveHost;
    private readonly Func<IPAddress, bool> _isIpAllowed;
    private readonly Func<int, bool> _isPortAllowed;

    /// <summary>Construct a fetcher whose handler enforces the connect-time IP guard.</summary>
    public SafeHttpFetcher(IOptions<SearXngOptions> options, ILogger<SafeHttpFetcher> logger)
        : this(options, logger, resolveHost: null, isIpAllowed: null)
    {
    }

    /// <summary>
    /// Test seam (security F5, <c>SafeHttpFetcherRedirectTests</c>): inject the DNS
    /// resolver, per-address vet, and per-port vet so the connect-time pin and the redirect
    /// re-vet are exercisable against loopback listeners on ephemeral ports - the public
    /// constructor always wires the production trio
    /// (<see cref="System.Net.Dns.GetHostAddressesAsync(string, CancellationToken)"/>
    /// + <see cref="SsrfGuard.IsIpAllowed"/> + <see cref="SsrfGuard.IsPortAllowed"/>).
    /// Internal on purpose: an options-based bypass would ship an SSRF hole.
    /// </summary>
    internal SafeHttpFetcher(
        IOptions<SearXngOptions> options,
        ILogger<SafeHttpFetcher> logger,
        Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolveHost,
        Func<IPAddress, bool>? isIpAllowed,
        Func<int, bool>? isPortAllowed = null)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resolveHost = resolveHost
            ?? (static (host, ct) => new ValueTask<IPAddress[]>(Dns.GetHostAddressesAsync(host, ct)));
        _isIpAllowed = isIpAllowed ?? SsrfGuard.IsIpAllowed;
        _isPortAllowed = isPortAllowed ?? SsrfGuard.IsPortAllowed;

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

            if (!_isPortAllowed(current.Port))
            {
                throw new SsrfBlockedException($"Port blocked by SSRF policy: {current.Port} ({current})");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await SendGuardedAsync(request, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Send, unwrapping a connect-time guard refusal: <see cref="GuardedConnectAsync"/>
    /// throws inside the handler, so <c>SocketsHttpHandler</c> surfaces it wrapped in an
    /// <see cref="HttpRequestException"/> - rethrow the typed <see cref="SsrfBlockedException"/>
    /// so callers (and the F5 tests) see the documented "blocked" contract, never a
    /// generic transport error.
    /// </summary>
    private async Task<HttpResponseMessage> SendGuardedAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SsrfBlockedException blocked)
        {
            throw new SsrfBlockedException(blocked.Message, ex);
        }
    }

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

        IPAddress[] resolved = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await _resolveHost(host, cancellationToken).ConfigureAwait(false);

        // IPv4-only policy (for now): drop any non-IPv4 answer (e.g. a dual-stack host's
        // AAAA records) before vetting, so an IPv6 address neither gets connected to nor
        // poisons the IPv4 path. We only ever open a socket to a vetted IPv4 address.
        var addresses = Array.FindAll(resolved, static a => a.AddressFamily == AddressFamily.InterNetwork);
        if (addresses.Length == 0)
        {
            _logger.LogWarning("SSRF guard blocked connect to {Host}: no IPv4 address (IPv6 unsupported).", host);
            throw new SsrfBlockedException($"No allowed IPv4 address for host: {host}");
        }

        foreach (var address in addresses)
        {
            if (!_isIpAllowed(address))
            {
                _logger.LogWarning("SSRF guard blocked connect to {Host} -> {Ip}.", host, address);
                throw new SsrfBlockedException($"Resolved address blocked by SSRF policy: {address}");
            }
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
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
