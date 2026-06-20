using Gert.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Tools.Search;

/// <summary>
/// Resolves the single <see cref="IWebSearch"/> backend selected via <c>Gert:Tools:Search:Type</c>
/// (default <c>SearXNG</c>) by building the <see cref="IWebSearchBuilder"/> plugin keyed by that
/// Type - keyed DI, no central <c>switch</c> (mirrors <c>Gert.Chat.ChatClientFactory</c>). A
/// selected Type with no registered plugin fails at first resolution with an actionable message.
/// </summary>
public sealed class WebSearchFactory
{
    /// <summary>The configuration key selecting the search backend.</summary>
    public const string TypeKey = "Gert:Tools:Search:Type";

    /// <summary>The backend assumed when <see cref="TypeKey"/> is unset (the only one shipped today).</summary>
    public const string DefaultType = "SearXNG";

    private readonly IServiceProvider _services;
    private readonly string _type;

    /// <summary>
    /// Capture the configured Type once (configuration is fixed for the host's lifetime). The
    /// registrar closes over <see cref="IConfiguration"/> rather than resolving it from DI, keeping
    /// the registration host-agnostic.
    /// </summary>
    public WebSearchFactory(IServiceProvider services, IConfiguration configuration)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[TypeKey];
        _type = string.IsNullOrWhiteSpace(configured) ? DefaultType : configured;
    }

    /// <summary>
    /// Normalize a configuration <c>Type</c> token to the keyed-plugin lookup key (each plugin
    /// registers under the same normalization), so casing in <c>appsettings.json</c> never matters.
    /// </summary>
    public static string NormalizeType(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Build the selected search backend by dispatching to its keyed plugin. Throws when the
    /// configured Type has no registered plugin (the unknown-backend failure mode).
    /// </summary>
    public IWebSearch Create() =>
        (_services.GetKeyedService<IWebSearchBuilder>(NormalizeType(_type))
            ?? throw new InvalidOperationException(
                $"{TypeKey} '{_type}' has no registered search plugin. Register its implementation " +
                "(e.g. AddGertSearchSearXNG) in the composition root. Use 'SearXNG'."))
        .Build();
}
