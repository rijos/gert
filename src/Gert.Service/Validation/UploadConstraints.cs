namespace Gert.Service.Validation;

/// <summary>
/// The upload allowlists / caps the document validator enforces (testing.md section 5:
/// extension &amp; content-type allowlist; max size; reject empty). Static today
/// (the values the design fixes); kept as a small type so a future config binding
/// or admin override is a one-line swap, and so the validator stays declarative.
/// </summary>
public static class UploadConstraints
{
    /// <summary>Maximum accepted upload size in bytes (DoS brake). 50 MiB.</summary>
    public const long MaxSizeBytes = 50L * 1024 * 1024;

    /// <summary>The accepted document extensions (lowercase, no dot) - pdf/docx/md/txt.</summary>
    public static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>(StringComparer.Ordinal) { "pdf", "docx", "md", "txt" };

    /// <summary>The accepted MIME types for those extensions (lowercased before lookup).</summary>
    public static readonly IReadOnlySet<string> AllowedMimeTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "application/pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "text/markdown",
            "text/x-markdown",
            "text/plain",
            // Browsers sometimes send a generic stream for unknown types; accept it
            // only because the extension allowlist + server-side sniffing are the
            // real gate (the extension check still rejects a disallowed file).
            "application/octet-stream",
        };
}
