using System.Net.Http.Json;
using System.Text.Json;
using Gert.Model;
using Gert.Model.Tools;
using Gert.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Tools.Sandbox.Monty;

/// <summary>
/// Real <see cref="IPythonSandbox"/> backed by the <b>monty</b> sidecar - Pydantic's minimal
/// Python interpreter written in Rust (chat-and-tools.md section sandbox; security F5). Monty
/// has <b>no syscalls</b>: there is no filesystem, network, or env access in the language
/// at all, so untrusted code can only reach the outside world through host callbacks -
/// and plain <c>run_python</c> grants none. The sidecar <i>process</i> is the OS-level
/// boundary wrapped around that (unprivileged, no <c>/data</c> mount, egress off): monty's
/// capability sandbox nested inside a process sandbox.
///
/// <para>
/// This adapter is a thin typed-<see cref="HttpClient"/> client - it POSTs the code plus
/// the shared per-run limits (<see cref="PythonSandboxOptions"/>) to the sidecar's <c>/run</c>
/// and maps the JSON back to a <see cref="PythonSandboxResult"/>. A transport failure or the
/// HTTP-timeout backstop is mapped to a graceful result by <see cref="MapFailure"/> - the
/// sandbox must never throw an infra error into the tool loop. <b>No automatic retries:</b>
/// a code run is not safely repeatable (and under code-mode would re-invoke tools), so the
/// only time bounds are monty's own wall clock and the HTTP-timeout backstop.
/// </para>
///
/// <para>
/// <b>Integration-only:</b> the live call needs a running monty sidecar. CI exercises the
/// pure <see cref="MapResponse"/> / <see cref="MapFailure"/> mapping and the FakeE2E mock
/// upstream; the real <c>pydantic-monty</c> path is validated on a host that has it.
/// </para>
/// </summary>
public sealed class MontySandbox : IPythonSandbox
{
    public const string HttpClientName = "monty";

    private readonly HttpClient _http;
    private readonly PythonSandboxOptions _options;
    private readonly ILogger<MontySandbox> _logger;

    public MontySandbox(HttpClient http, IOptions<PythonSandboxOptions> options, ILogger<MontySandbox> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<PythonSandboxResult> RunPythonAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        var request = new MontyRunRequest
        {
            Code = code,
            WallClockSeconds = _options.WallClockSeconds,
            MemoryMiB = _options.MemoryMiB,
            MaxOutputBytes = _options.MaxOutputBytes,
        };

        try
        {
            using var response = await _http.PostAsJsonAsync("/run", request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await ReadCappedResponseAsync(response, MaxResponseBytes(_options), cancellationToken)
                .ConfigureAwait(false);
            return MapResponse(payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled (e.g. the turn was abandoned) - propagate, don't swallow.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "monty sidecar /run failed; returning a graceful result.");
            return MapFailure(ex);
        }
    }

    /// <summary>
    /// The byte bound for a <c>/run</c> response (capped reads are the standard for
    /// every external body - style guide section 9): the sidecar caps stdout and stderr at
    /// <see cref="PythonSandboxOptions.MaxOutputBytes"/> each, so a healthy response is at
    /// most the two capped streams times worst-case JSON escaping (6x, <c>\uXXXX</c>)
    /// plus a small envelope. Anything larger is a misbehaving sidecar.
    /// </summary>
    private static long MaxResponseBytes(PythonSandboxOptions options) =>
        ((long)options.MaxOutputBytes * 2 * 6) + 4096;

    /// <summary>
    /// Read and deserialize the <c>/run</c> body with an enforced size bound, so a
    /// misbehaving sidecar cannot balloon host memory. An over-bound body throws
    /// <see cref="HttpRequestException"/>, which the caller maps to a graceful
    /// <see cref="PythonSandboxResult"/> via <see cref="MapFailure"/>.
    /// </summary>
    private static async Task<MontyRunResponse?> ReadCappedResponseAsync(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                throw new HttpRequestException(
                    $"monty sidecar /run response exceeded the {maxBytes}-byte bound.");
            }
        }

        buffer.Position = 0;
        return await JsonSerializer.DeserializeAsync<MontyRunResponse>(
            buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Map the sidecar's JSON response to a <see cref="PythonSandboxResult"/>. A null/empty body
    /// (a sidecar bug) degrades to a non-zero exit the model can read rather than a throw.
    /// Pure + unit-tested.
    /// </summary>
    public static PythonSandboxResult MapResponse(MontyRunResponse? payload)
    {
        if (payload is null)
        {
            return new PythonSandboxResult
            {
                ExitCode = 1,
                Stderr = "monty sidecar returned an empty response.",
            };
        }

        return new PythonSandboxResult
        {
            ExitCode = payload.ExitCode,
            Stdout = payload.Stdout,
            Stderr = payload.Stderr,
            TimedOut = payload.TimedOut,
        };
    }

    /// <summary>
    /// Map a transport failure - sidecar unreachable, a non-success status, or the
    /// HTTP-timeout backstop - to a graceful <see cref="PythonSandboxResult"/>. By the time we
    /// get here a caller cancellation has already been re-thrown, so a remaining
    /// cancellation/timeout means the run overran: a timed-out result. Pure + unit-tested.
    /// </summary>
    public static PythonSandboxResult MapFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is TimeoutException or OperationCanceledException)
        {
            return new PythonSandboxResult
            {
                ExitCode = 124, // conventional timeout exit code
                Stderr = "Sandbox run exceeded the wall-clock limit and was terminated.",
                TimedOut = true,
            };
        }

        return new PythonSandboxResult
        {
            ExitCode = 1,
            Stderr = $"Sandbox run failed: {exception.Message}",
        };
    }
}
