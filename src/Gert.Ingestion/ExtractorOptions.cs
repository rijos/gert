namespace Gert.Ingestion;

/// <summary>
/// The document text-extractor functionality (<c>Gert:Extractor</c>): pick an implementation
/// via <see cref="Type"/>, configure it under <see cref="Parameters"/> - the uniform
/// "functionality -> Type -> Parameters" shape (configuration.md section 4; security F7). Only
/// <c>Subprocess</c> ships today (an unprivileged, resource-capped PDF/DOCX helper). The caps +
/// hardening knobs live in <see cref="Parameters"/>.
/// </summary>
public sealed class ExtractorOptions
{
    public const string SectionName = "Gert:Extractor";

    /// <summary>The extractor implementation to use. <c>Subprocess</c> today.</summary>
    public string Type { get; set; } = "Subprocess";

    /// <summary>The implementation's caps + hardening (what changes when <see cref="Type"/> changes).</summary>
    public ExtractorParameters Parameters { get; set; } = new();
}
