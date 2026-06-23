using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gert.Service.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Ingestion.Subprocess;

/// <summary>
/// Real PDF/DOCX/XLSX <see cref="ITextExtractor"/> (security F7): extraction runs in an
/// <b>unprivileged, resource-capped subprocess</b> - dropped privs, no network,
/// <c>RLIMIT_AS</c>/<c>CPU</c>/<c>NPROC</c> + a wall-clock kill - so a memory-corruption
/// or resource-exhaustion bug in PdfPig/OpenXML cannot take down the host. DTD /
/// external-entity resolution is off (<see cref="HardenedXml"/>) and the DOCX zip is
/// bounded by <see cref="ZipBombGuard"/>.
///
/// <para>
/// A crash, OOM, timeout, or absent helper binary fails <b>that document</b>
/// (<see cref="ExtractionResult.Failed"/>) and <b>never throws to the host</b>. The
/// PdfPig/OpenXML parse lives in the helper executable (a separate deliverable, currently
/// stubbed behind the subprocess boundary; see TODO in <see cref="RunHelperAsync"/>).
/// </para>
/// </summary>
public sealed class IsolatedTextExtractor : ITextExtractor
{
    // The binary document formats parsed in the isolated subprocess (the single source of
    // truth: DocumentFormats.IsolatedExtensions - pdf/docx/xlsx). xlsx is recognised here but
    // only extracts once the gert-extract helper ships (see RunHelperAsync); until then it
    // fails the document cleanly, as pdf/docx already do.
    private static readonly IReadOnlySet<string> Handled = Gert.Model.Documents.DocumentFormats.IsolatedExtensions;

    private readonly ExtractorParameters _options;
    private readonly ILogger<IsolatedTextExtractor> _logger;

    public IsolatedTextExtractor(IOptions<ExtractorOptions> options, ILogger<IsolatedTextExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Parameters;
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
    /// <see cref="ExtractionResult"/>. A non-zero exit or unparseable output ->
    /// <see cref="ExtractionResult.Failed"/>.
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

    public static bool HelperAvailable(ExtractorParameters options)
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
        // The helper applies the rlimits + drops privs + sets no-network, then parses
        // with PdfPig (pdf) or OpenXML (docx/xlsx) using HardenedXml + ZipBombGuard, and emits
        // {"pages":[...]}.
        // TODO: ship the `gert-extract` helper executable; until then the
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

        // Bounded reads: a flooding or compromised helper must not balloon host memory (F7 - helper
        // misbehaviour cannot harm the host). Stop after MaxOutputBytes from stdout (a smaller cap on
        // stderr); the helper self-caps via --max-output, this is the host-side enforcement. The
        // unread remainder fills the OS pipe and the helper blocks on write; the wall-clock timeout
        // below then reaps it.
        var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, _options.MaxOutputBytes, cancellationToken);
        var stderrTask = ReadCappedAsync(process.StandardError.BaseStream, StderrMaxBytes, cancellationToken);

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
        return ParseHelperOutput(process.ExitCode, stdout, stderr);
    }

    /// <summary>Stderr is just an error reason; cap it well below the stdout budget.</summary>
    private const int StderrMaxBytes = 64 * 1024;

    /// <summary>
    /// Read at most <paramref name="maxBytes"/> bytes from a helper output stream, then stop - so a
    /// flooding/compromised helper cannot balloon host memory (security F7); the cap is enforced
    /// DURING the read, never after a full buffer. Bytes, not chars, is the unit (matching
    /// <c>MaxOutputBytes</c> and the helper's <c>--max-output</c>); the captured bytes decode as
    /// UTF-8 - a truncated multi-byte tail degrades to a replacement char, which
    /// <see cref="ParseHelperOutput"/> then rejects as invalid JSON (that document fails, the host is fine).
    /// </summary>
    private static async Task<string> ReadCappedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        var cap = (int)Math.Min(maxBytes, int.MaxValue);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while (buffer.Length < cap &&
               (read = await stream
                   .ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, cap - buffer.Length)), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
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
