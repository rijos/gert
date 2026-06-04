using Gert.Model.Dtos;
using Gert.Service;
using Gert.Service.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Console;

/// <summary>
/// The Console command surface — a deliberately tiny CLI over <see cref="IGertServices"/>
/// (tech-stack.md § Architecture: the Console drives the same services directly).
/// Two commands:
/// <list type="bullet">
///   <item>
///     <c>chat "&lt;message&gt;" [--project &lt;pid&gt;]</c> — creates a conversation,
///     runs <c>StartTurnAsync</c> + <c>RunAsync</c>, and renders the
///     <see cref="Model.Events.ChatEvent"/> stream to stdout via
///     <see cref="ConsoleChatRenderer"/>.
///   </item>
///   <item>
///     <c>ingest &lt;path&gt; [--project &lt;pid&gt;]</c> — reads a file and calls
///     <c>UploadAsync</c>; ingestion runs <b>inline</b> (the inline queue), so the
///     returned document already carries its terminal status, which is printed.
///   </item>
/// </list>
/// </summary>
public sealed class ConsoleApp
{
    private const string DefaultProject = "default";

    private readonly IServiceProvider _services;
    private readonly TextWriter _out;
    private readonly TextWriter _error;

    /// <summary>Build the app over a configured root provider and the output writers.</summary>
    public ConsoleApp(IServiceProvider services, TextWriter output, TextWriter error)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _out = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
    }

    /// <summary>
    /// Dispatch on <paramref name="args"/>; returns a process exit code (0 success,
    /// 1 usage/runtime error). Validation/runtime exceptions are reported on stderr.
    /// </summary>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return Usage();
        }

        try
        {
            return args[0] switch
            {
                "chat" => await ChatAsync(args, cancellationToken).ConfigureAwait(false),
                "ingest" => await IngestAsync(args, cancellationToken).ConfigureAwait(false),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            // The service layer's ValidationException (and any runtime error) surfaces
            // here — the SAME rejection the API renders as 400 (testing.md §7).
            _error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> ChatAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2 || string.IsNullOrEmpty(args[1]))
        {
            _error.WriteLine("usage: gert chat \"<message>\" [--project <pid>]");
            return 1;
        }

        var message = args[1];
        var pid = OptionValue(args, "--project") ?? DefaultProject;

        await using var scope = _services.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();

        var conversation = await gert.Conversations
            .CreateAsync(pid, new CreateConversationRequest(), cancellationToken)
            .ConfigureAwait(false);

        var turn = await gert.Chat
            .StartTurnAsync(
                pid,
                conversation.Id,
                new SendMessageRequest { Content = message },
                cancellationToken)
            .ConfigureAwait(false);

        var renderer = new ConsoleChatRenderer(_out, _error);
        await renderer.RenderAsync(gert.Chat.RunAsync(turn, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        return 0;
    }

    private async Task<int> IngestAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2 || string.IsNullOrEmpty(args[1]))
        {
            _error.WriteLine("usage: gert ingest <path> [--project <pid>]");
            return 1;
        }

        var path = args[1];
        var pid = OptionValue(args, "--project") ?? DefaultProject;

        if (!File.Exists(path))
        {
            _error.WriteLine($"error: file not found: {path}");
            return 1;
        }

        var filename = Path.GetFileName(path);
        var info = new FileInfo(path);

        var upload = new DocumentUpload
        {
            Filename = filename,
            Mime = MimeForExtension(Path.GetExtension(path)),
            SizeBytes = info.Length,
            OpenReadStream = () => File.OpenRead(path),
        };

        await using var scope = _services.CreateAsyncScope();
        var gert = scope.ServiceProvider.GetRequiredService<IGertServices>();

        // Ingestion runs inline (the inline queue) during Upload, but UploadAsync
        // returns the pre-ingestion row — re-fetch for the terminal status/chunk count.
        var created = await gert.Documents.UploadAsync(pid, upload, cancellationToken)
            .ConfigureAwait(false);
        var document = await gert.Documents.GetAsync(pid, created.Id, cancellationToken)
            .ConfigureAwait(false) ?? created;

        // documents.Filename is base64 (display metadata, never a path) — decode for output.
        var name = DecodeFilename(document.Filename);
        _out.WriteLine(
            $"{name}: {document.Status} ({document.ChunkCount} chunks)"
            + (document.Error is { Length: > 0 } err ? $" — {err}" : string.Empty));

        return 0;
    }

    /// <summary>Map a file extension to its allowed MIME type (UploadConstraints).</summary>
    private static string MimeForExtension(string extension) =>
        extension.TrimStart('.').ToLowerInvariant() switch
        {
            "pdf" => "application/pdf",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "md" => "text/markdown",
            "txt" => "text/plain",
            _ => "application/octet-stream",
        };

    /// <summary>Decode the base64 display filename stored in <c>documents.filename</c>;
    /// fall back to the raw value if it isn't valid base64.</summary>
    private static string DecodeFilename(string stored)
    {
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(stored));
        }
        catch (FormatException)
        {
            return stored;
        }
    }

    /// <summary>Read the value following <paramref name="name"/> in <paramref name="args"/>, or null.</summary>
    private static string? OptionValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private int Usage()
    {
        _error.WriteLine("usage:");
        _error.WriteLine("  gert chat \"<message>\" [--project <pid>]");
        _error.WriteLine("  gert ingest <path> [--project <pid>]");
        return 1;
    }
}
