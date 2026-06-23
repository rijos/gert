namespace Gert.Validation.Rules;

/// <summary>
/// The upload caps the document validator enforces (testing.md section 5: max size; non-empty;
/// filename length). Gert accepts <b>any</b> file: type safety is no longer an upload-time
/// extension/MIME allowlist - it is enforced where the bytes are read. Unknown content is only
/// ever UTF-8 decoded in-process (and rejected if it is not text); the binary document formats
/// (<see cref="Gert.Model.Documents.DocumentFormats.IsolatedExtensions"/>) are parsed only inside
/// the isolated F7 subprocess. Kept as a small type so a future config binding or admin override
/// is a one-line swap, and so the validator stays declarative.
/// </summary>
public static class UploadConstraints
{
    /// <summary>Maximum accepted upload size in bytes (DoS brake). 50 MiB.</summary>
    public const long MaxSizeBytes = 50L * 1024 * 1024;
}
