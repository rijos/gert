using System.Xml;

namespace Gert.Ingestion.Subprocess;

/// <summary>
/// XML-hardening for the DOCX path (security F7): DTD processing and external-entity
/// resolution are <b>off</b>, defeating XXE. The OOXML format is a zip of XML parts, so
/// any XML the extractor parses must use these settings. Exposed as a pure factory so
/// the flags are unit-assertable without parsing a real document.
/// </summary>
public static class HardenedXml
{
    /// <summary>
    /// <see cref="XmlReaderSettings"/> with DTDs prohibited and no external resolver -
    /// the XXE-safe configuration for parsing untrusted OOXML parts.
    /// </summary>
    public static XmlReaderSettings CreateSafeSettings() => new()
    {
        // Prohibit DTDs entirely - no DOCTYPE, so no entity expansion at all.
        DtdProcessing = DtdProcessing.Prohibit,
        // No resolver -> external entities / external DTDs cannot be fetched.
        XmlResolver = null,
        CloseInput = true,
        // Belt-and-braces entity-expansion cap, should a future change ever relax
        // DtdProcessing above (0 means "no limit", so we set a concrete ceiling).
        MaxCharactersFromEntities = 1024,
    };

    public static XmlReader CreateReader(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return XmlReader.Create(input, CreateSafeSettings());
    }
}
