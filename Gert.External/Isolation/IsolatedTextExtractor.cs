using System.Diagnostics;
using System.Text.Json;
using Gert.Service.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.External.Isolation;

/// <summary>
/// Real PDF/DOCX <see cref="ITextExtractor"/> (security F7): extraction runs in an
/// <b>unprivileged, resource-capped subprocess</b> — dropped privs, no network,
/// <c>RLIMIT_AS</c>/<c>CPU</c>/<c>NPROC</c> + a wall-clock kill — so a memory-corruption
/// or resource-exhaustion bug in PdfPig/OpenXML cannot take down the host. DTD /
/// external-entity resolution is off (<see cref="HardenedXml"/>) and the DOCX zip is
/// bounded by <see cref="ZipBombGuard"/>. This fills the pdf/docx leaf the U7d
/// <c>CompositeTextExtractor</c> currently routes to "not available".
///
/// <para>
/// A crash, OOM, or timeout fails <b>that document</b> (<see cref="ExtractionResult.Failed"/>)
/// and <b>never throws to the host</b>. When the helper binary is absent (CI / a dev box
/// without it), extraction also fails gracefully.
/// </para>
///
/// <para>
/// <b>Unit-tested:</b> <see cref="CanExtract"/>, the helper-output → result mapping
/// (<see cref="ParseHelperOutput"/>), the failure → <see cref="ExtractionResult.Failed"/>
/// mapping, plus <see cref="ExtractorCommandBuilder"/> / <see cref="ZipBombGuard"/> /
/// <see cref="HardenedXml"/>. <b>Integration-only:</b> the live subprocess spawn + the
/// real PdfPig/OpenXML parse inside it (U13 / staging). The actual parse is currently
/// <b>stubbed behind the subprocess boundary</b> — the helper executable (which calls
/// PdfPig/OpenXML with the rlimits + hardening) is a separate deliverable; see TODO below.
/// </para>
/// </summary>
public sealed class IsolatedTextExtractor : ITextExtractor
{
    private static readonly IReadOnlySet<string> Handled =
        new HashSet<string>(StringComparer.Ordinal) { "pdf", "docx" };

    private readonly ExtractorOptions _options;
    private readonly ILogger<IsolatedTextExtractor> _logger;

    /// <summary>Construct over the configured caps.</summary>
    public IsolatedTextExtractor(IOptions<ExtractorOptions> options, ILogger<IsolatedTextExtractor> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool CanExtract(string extension) =>
        extension is not null && Handled.Contains(extension);

    /// <inheritdoc />
    public async Task<ExtractionResult> ExtractAsync(
        Stream content,
        string extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!CanExtract(extension))
        {
            return ExtractionResult.Failed($"No isolated extractor for '.{extension}' files.");
        }

        if (!HelperAvailable(_options))
        {
            _logger.LogWarning("Extractor helper ({Helper}) not available.", _options.HelperPath);
            return ExtractionResult.Failed("Isolated extractor helper is not available on this host.");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"gert-extract-{Guid.NewGuid():N}.{extension}");
        try
        {
            await using (var file = File.Create(tempPath))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            return await RunHelperAsync(extension, tempPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // F7: a crash/timeout/IO error fails THIS document, never the host.
            _logger.LogWarning(ex, "Isolated extraction failed for '.{Ext}'.", extension);
            return ExtractionResult.Failed($"Extraction failed: {ex.Message}");
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Map the helper's stdout (a JSON document of pages) to an
    /// <see cref="ExtractionResult"/>. A non-zero exit or unparseable output →
    /// <see cref="ExtractionResult.Failed"/>. Pure + unit-tested.
    /// </summary>
    public static ExtractionResult ParseHelperOutput(int exitCode, string stdout, string stderr)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (exitCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? $"helper exit {exitCode}" : stderr.Trim();
            return ExtractionResult.Failed($"Extractor helper failed: {reason}");
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            return ExtractionResult.Failed("Extractor helper produced no output.");
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            if (!root.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            {
                return ExtractionResult.Failed("Extractor helper output missing 'pages'.");
            }

            var list = new List<ExtractedPage>();
            foreach (var p in pages.EnumerateArray())
            {
                var text = p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? string.Empty
                    : string.Empty;
                var locator = p.TryGetProperty("locator", out var l) && l.ValueKind == JsonValueKind.String
                    ? l.GetString()
                    : null;

                list.Add(new ExtractedPage { Text = text, Locator = locator });
            }

            return ExtractionResult.FromPages(list);
        }
        catch (JsonException ex)
        {
            return ExtractionResult.Failed($"Extractor helper output was not valid JSON: {ex.Message}");
        }
    }

    /// <summary>Detect whether the extractor helper binary is on the host.</summary>
    public static bool HelperAvailable(ExtractorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (Path.IsPathRooted(options.HelperPath))
        {
            return File.Exists(options.HelperPath);
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return false;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, options.HelperPath)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<ExtractionResult> RunHelperAsync(string extension, string inputPath, CancellationToken cancellationToken)
    {
        // INTEGRATION-ONLY: spawn the unprivileged helper. The helper applies the
        // rlimits + drops privs + sets no-network, then parses with PdfPig (pdf) or
        // OpenXML (docx) using HardenedXml + ZipBombGuard, and emits {"pages":[...]}.
        // TODO(U13/staging): ship the `gert-extract` helper executable; until then the
        // PdfPig/OpenXML parse lives behind this subprocess boundary (stubbed here).
        var args = ExtractorCommandBuilder.BuildArgs(_options, extension, inputPath);

        var psi = new ProcessStartInfo
        {
            FileName = _options.HelperPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var wall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wall.CancelAfter(TimeSpan.FromSeconds(_options.WallClockSeconds));

        try
        {
            await process.WaitForExitAsync(wall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return ExtractionResult.Failed("Extraction timed out and the helper was terminated.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return ParseHelperOutput(process.ExitCode, Cap(stdout), stderr);
    }

    private string Cap(string text)
    {
        if (text.Length <= _options.MaxOutputBytes)
        {
            return text;
        }

        return text[..(int)Math.Min(_options.MaxOutputBytes, int.MaxValue)];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
