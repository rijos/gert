using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Gert.Tools.Builtin.Fetch;
using Gert.Tools.Builtin.Search.SearXNG;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Tools.Builtin.Tests;

/// <summary>
/// The <see cref="SafeWebFetcher"/> adapter's exception -> result mapping (the
/// <c>IWebFetcher</c> port's never-throws contract): a policy block, a transport
/// failure, and a non-2xx all come back as <c>Success=false</c> with a readable
/// error (the web_fetch tool's card-visible TOOL ERROR - security F5), while a
/// plain success returns the body. The fetcher's own controls are covered by
/// <see cref="SafeHttpFetcherTests"/> / <see cref="SafeHttpFetcherRedirectTests"/>;
/// the loopback cases here reuse the same internal test seam.
/// </summary>
public sealed class SafeWebFetcherTests
{
    private static readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> AllHostsToLoopback =
        static (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Loopback]);

    private static SafeWebFetcher ProductionFetcher() =>
        new(new SafeHttpFetcher(
            Options.Create(new SearXngOptions()),
            NullLogger<SafeHttpFetcher>.Instance));

    /// <summary>Loopback-permitting fetcher via the internal F5 test seam.</summary>
    private static SafeWebFetcher LoopbackFetcher() =>
        new(new SafeHttpFetcher(
            Options.Create(new SearXngOptions()),
            NullLogger<SafeHttpFetcher>.Instance,
            AllHostsToLoopback,
            isIpAllowed: _ => true,
            // Loopback listeners bind ephemeral ports, so open the 80/443 scope here too
            // (the port control itself is covered by SafeHttpFetcherTests + SsrfGuardTests).
            isPortAllowed: static _ => true));

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task A_policy_blocked_url_maps_to_the_blocked_result(string url)
    {
        var fetcher = ProductionFetcher();

        var result = await fetcher.FetchAsync(url);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("URL blocked by fetch policy");
    }

    [Fact]
    public async Task A_connection_failure_maps_to_fetch_failed()
    {
        // A loopback port with no listener: connect is refused -> HttpRequestException.
        using var reserved = new TcpListener(IPAddress.Loopback, 0);
        reserved.Start();
        var deadPort = ((IPEndPoint)reserved.LocalEndpoint).Port;
        reserved.Stop();

        var fetcher = LoopbackFetcher();

        var result = await fetcher.FetchAsync($"http://origin.test:{deadPort}/");

        result.Success.Should().BeFalse();
        result.Error.Should().StartWith("fetch failed");
    }

    [Fact]
    public async Task A_non_2xx_status_maps_to_fetch_failed_with_the_code()
    {
        await using var server = new OneShotHttpServer(
            "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var fetcher = LoopbackFetcher();

        var result = await fetcher.FetchAsync($"http://origin.test:{server.Port}/missing");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("fetch failed (404)");
    }

    [Fact]
    public async Task A_successful_fetch_returns_the_body()
    {
        const string body = "fetched-ok";
        await using var server = new OneShotHttpServer(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
            "Connection: close\r\n\r\n" +
            body);
        var fetcher = LoopbackFetcher();

        var result = await fetcher.FetchAsync($"http://origin.test:{server.Port}/page");

        result.Success.Should().BeTrue();
        result.Content.Should().Be(body);
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task The_callers_cancellation_still_throws()
    {
        var fetcher = ProductionFetcher();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Cancellation is the turn's signal - the never-throws contract covers
        // fetch outcomes only, so an already-cancelled token must surface as OCE.
        var act = () => fetcher.FetchAsync("https://example.test/", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Minimal single-response loopback HTTP listener (the slim sibling of
    /// <c>SafeHttpFetcherRedirectTests.LoopbackHttpServer</c>): reads one request's
    /// headers, writes the canned response, closes.
    /// </summary>
    private sealed class OneShotHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;

        public OneShotHttpServer(string response)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = ServeAsync(response, _stop.Token);
        }

        public int Port { get; }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _serve.ConfigureAwait(false); // serve loop swallows its shutdown exceptions
            _stop.Dispose();
        }

        private async Task ServeAsync(string response, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var client = await _listener
                        .AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
                    var bytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown via DisposeAsync.
            }
            catch (SocketException)
            {
                // Listener stopped mid-accept - shutdown path.
            }
            catch (ObjectDisposedException)
            {
                // Listener disposed mid-accept - shutdown path.
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
