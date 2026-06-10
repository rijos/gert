using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Gert.External.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.External.Tests;

/// <summary>
/// The two <see cref="SafeHttpFetcher"/> controls that only exist with a live socket
/// (security F5): the per-hop redirect re-vet and the connect-time DNS pin. They run
/// fully local through the fetcher's <b>internal</b> test seam — injected
/// <c>resolveHost</c> / <c>isIpAllowed</c> delegates map fake "public" hostnames onto
/// loopback listeners (which the real guard rightly blocks), while the production
/// wiring (real DNS + <see cref="SsrfGuard.IsIpAllowed"/>) stays untouched and
/// non-configurable. <see cref="SafeHttpFetcherTests"/> covers the pre-socket URL
/// vetting; <see cref="SsrfGuardTests"/> covers the pure policy.
/// </summary>
public sealed class SafeHttpFetcherRedirectTests
{
    private static readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> AllHostsToLoopback =
        static (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Loopback]);

    private static SafeHttpFetcher NewFetcher(
        Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolveHost,
        Func<IPAddress, bool>? isIpAllowed) =>
        new(
            Options.Create(new SearXngOptions()),
            NullLogger<SafeHttpFetcher>.Instance,
            resolveHost,
            isIpAllowed);

    // --- Test A: redirect re-vet (F5: every hop is re-checked) ----------------

    [Fact]
    public async Task Redirect_into_a_private_literal_is_blocked_and_the_target_is_never_contacted()
    {
        await using var target = new LoopbackHttpServer(_ => Ok("secret"));
        await using var origin = new LoopbackHttpServer(
            _ => Redirect($"http://127.0.0.1:{target.Port}/secret"));
        using var fetcher = NewFetcher(AllHostsToLoopback, isIpAllowed: _ => true);

        var act = () => fetcher.FetchAsync($"http://origin.test:{origin.Port}/start");

        (await act.Should().ThrowAsync<SsrfBlockedException>())
            .WithMessage("*blocked by SSRF policy*");
        origin.RequestCount.Should().Be(1, "the first hop is allowed and served");
        target.RequestCount.Should().Be(0, "the re-vetted redirect target must never be contacted");
    }

    [Fact]
    public async Task Redirect_chain_longer_than_the_cap_fails_with_too_many_redirects()
    {
        var selfLocation = string.Empty; // assigned below, read lazily per request
        await using var origin = new LoopbackHttpServer(_ => Redirect(selfLocation));
        selfLocation = $"http://origin.test:{origin.Port}/start";
        using var fetcher = NewFetcher(AllHostsToLoopback, isIpAllowed: _ => true);

        var act = () => fetcher.FetchAsync(selfLocation);

        (await act.Should().ThrowAsync<SsrfBlockedException>())
            .WithMessage("*Too many redirects*");
        // MaxRedirects (default 3) caps the loop: the initial request + 3 followed hops.
        origin.RequestCount.Should().Be(4);
    }

    [Fact]
    public async Task Allowed_redirect_to_another_public_looking_host_is_still_fetched()
    {
        await using var target = new LoopbackHttpServer(_ => Ok("fetched-ok"));
        await using var origin = new LoopbackHttpServer(
            _ => Redirect($"http://other.test:{target.Port}/"));
        using var fetcher = NewFetcher(AllHostsToLoopback, isIpAllowed: _ => true);

        var body = await fetcher.FetchAsync($"http://origin.test:{origin.Port}/start");

        body.Should().Be("fetched-ok", "an allowed redirect must keep working — the guard only blocks bad hops");
        origin.RequestCount.Should().Be(1);
        target.RequestCount.Should().Be(1);
    }

    // --- Test B: connect-time DNS pin (F5: rebind-proof, no server needed) ----

    [Fact]
    public async Task Host_resolving_to_a_private_address_is_blocked_by_the_real_guard_as_a_typed_refusal()
    {
        // Real default isIpAllowed (null → SsrfGuard.IsIpAllowed); only DNS is faked.
        using var fetcher = NewFetcher(
            static (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("10.0.0.1")]),
            isIpAllowed: null);

        var act = () => fetcher.FetchAsync("http://rebind.test/");

        // Specifically the typed F5 exception — not an HttpRequestException transport
        // wrapper — so callers can keep treating "blocked" as "skip this result".
        (await act.Should().ThrowAsync<SsrfBlockedException>())
            .WithMessage("*Resolved address blocked*10.0.0.1*");
    }

    [Fact]
    public async Task Host_resolving_to_a_mixed_public_and_private_list_is_blocked()
    {
        // Every resolved address must pass — one private entry poisons the lot,
        // otherwise a rebinding resolver could smuggle the private one in.
        using var fetcher = NewFetcher(
            static (_, _) => ValueTask.FromResult<IPAddress[]>(
                [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.1")]),
            isIpAllowed: null);

        var act = () => fetcher.FetchAsync("http://rebind.test/");

        await act.Should().ThrowAsync<SsrfBlockedException>();
    }

    // --- Test C: combined — allowed origin, internal redirect target ----------

    [Fact]
    public async Task Allowed_origin_redirecting_to_an_internal_host_is_blocked_at_the_second_hop()
    {
        await using var origin = new LoopbackHttpServer(_ => Redirect("http://internal.test/"));
        using var fetcher = NewFetcher(
            resolveHost: (host, _) => ValueTask.FromResult<IPAddress[]>(
                host == "internal.test" ? [IPAddress.Parse("192.168.1.1")] : [IPAddress.Loopback]),
            // Loopback is the test-harness carve-out; every other address gets the real guard.
            isIpAllowed: ip => IPAddress.IsLoopback(ip) || SsrfGuard.IsIpAllowed(ip));

        var act = () => fetcher.FetchAsync($"http://origin.test:{origin.Port}/start");

        (await act.Should().ThrowAsync<SsrfBlockedException>())
            .WithMessage("*Resolved address blocked*192.168.1.1*");
        origin.RequestCount.Should().Be(1, "only the vetted first hop is ever served");
    }

    // --- Canned wire responses -------------------------------------------------

    private static string Ok(string body) =>
        "HTTP/1.1 200 OK\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
        "Connection: close\r\n\r\n" +
        body;

    private static string Redirect(string location) =>
        "HTTP/1.1 302 Found\r\n" +
        $"Location: {location}\r\n" +
        "Content-Length: 0\r\n" +
        "Connection: close\r\n\r\n";

    /// <summary>
    /// Minimal one-connection-at-a-time HTTP listener on a free loopback port.
    /// <see cref="TcpListener"/> (not <see cref="HttpListener"/>) so the port is known
    /// race-free before any prefix registration; every response carries
    /// <c>Connection: close</c>, which forces the fetcher to reconnect per hop — i.e.
    /// the connect-time guard genuinely runs for every redirect.
    /// </summary>
    private sealed class LoopbackHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<int, string> _responseFor;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptLoop;
        private int _requests;

        public LoopbackHttpServer(Func<int, string> responseFor)
        {
            _responseFor = responseFor;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync(_stop.Token);
        }

        public int Port { get; }

        public int RequestCount => Volatile.Read(ref _requests);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _acceptLoop.ConfigureAwait(false); // loop swallows its shutdown exceptions
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var client = await _listener
                        .AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
                    var index = Interlocked.Increment(ref _requests) - 1;
                    var response = Encoding.ASCII.GetBytes(_responseFor(index));
                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown via DisposeAsync — the loop's owner is awaiting us.
            }
            catch (SocketException)
            {
                // Listener stopped mid-accept — shutdown path.
            }
            catch (ObjectDisposedException)
            {
                // Listener disposed mid-accept — shutdown path.
            }
        }

        private static async Task ReadRequestHeadersAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            var seen = new StringBuilder();
            while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return; // client went away; nothing to answer
                }

                seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
        }
    }
}
