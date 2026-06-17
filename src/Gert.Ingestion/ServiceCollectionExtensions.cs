using Gert.Ingestion.PlainText;
using Gert.Ingestion.Subprocess;
using Gert.Service.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Gert.Ingestion;

/// <summary>
/// One-call DI registration for the document ingestion adapter (tech-stack.md section
/// Architecture: <c>AddGertIngestion(cfg)</c>): the md/txt <see cref="PlainTextExtractor"/>
/// and the isolated PDF/DOCX text extractor (<c>Gert:Extractor</c>; security F7). The
/// service layer keeps talking only to the <see cref="ITextExtractor"/> port - this
/// registers both leaves under the same key the <c>CompositeTextExtractor</c> enumerates.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Register the isolated extractor leaf + options.</summary>
    public static IServiceCollection AddGertIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ExtractorOptions>()
            .Bind(configuration.GetSection(ExtractorOptions.SectionName))
            .ValidateOnStart();
        // Fail-closed Type discriminator: an unknown extractor Type fails fast at startup.
        services.AddSingleton<IValidateOptions<ExtractorOptions>, ExtractorTypeValidator>();

        // Register both leaves under the SAME key the CompositeTextExtractor
        // enumerates (Gert.Service.ServiceCollectionExtensions.LeafExtractorKey): the
        // composite routes md/txt to the plain extractor and pdf/docx to the isolated one.
        services.AddKeyedSingleton<ITextExtractor, PlainTextExtractor>(
            Gert.Service.ServiceCollectionExtensions.LeafExtractorKey);
        services.AddKeyedSingleton<ITextExtractor, IsolatedTextExtractor>(
            Gert.Service.ServiceCollectionExtensions.LeafExtractorKey);

        return services;
    }

    /// <summary>
    /// Fail-closed <see cref="ExtractorOptions.Type"/> discriminator (configuration.md
    /// section 4): only <c>Subprocess</c> ships today, so an unknown Type fails fast at startup.
    /// </summary>
    private sealed class ExtractorTypeValidator : IValidateOptions<ExtractorOptions>
    {
        public ValidateOptionsResult Validate(string? name, ExtractorOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (!string.Equals(options.Type, "Subprocess", StringComparison.OrdinalIgnoreCase))
            {
                return ValidateOptionsResult.Fail(
                    $"{ExtractorOptions.SectionName}:Type '{options.Type}' is not supported. Use 'Subprocess'.");
            }

            return ValidateOptionsResult.Success;
        }
    }
}
