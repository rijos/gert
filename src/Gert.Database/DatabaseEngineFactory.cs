using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gert.Database;

/// <summary>
/// Resolves the single database engine the operator selected via <c>Gert:Database:Type</c>
/// (default <c>Sqlite</c>): it looks up the registered <see cref="IDatabaseEngineBuilder"/>
/// plugin keyed by that Type and delegates - keyed DI, no central <c>switch</c> over Type
/// (mirrors <c>Gert.Chat.ChatClientFactory</c> / <c>Gert.Tools.PythonSandboxFactory</c>). The
/// composition root registers the shipped engine plugin (<c>AddGertDatabaseSqlite</c>); a
/// selected Type with no registered plugin fails at first resolution with an actionable message.
/// The engine is resolved once and cached - the providers it builds are process-wide singletons.
/// </summary>
public sealed class DatabaseEngineFactory
{
    /// <summary>The configuration key selecting the database engine.</summary>
    public const string TypeKey = "Gert:Database:Type";

    /// <summary>The engine assumed when <see cref="TypeKey"/> is unset (per-user SQLite files).</summary>
    public const string DefaultType = "Sqlite";

    private readonly IServiceProvider _services;
    private readonly string _type;
    private IDatabaseEngineBuilder? _engine;

    /// <summary>
    /// Capture the configured Type once - configuration is fixed for the host's lifetime
    /// (constructed over the closed-over <see cref="IConfiguration"/> in the registrar, keeping
    /// the registration host-agnostic rather than resolving <see cref="IConfiguration"/> from DI).
    /// </summary>
    public DatabaseEngineFactory(IServiceProvider services, IConfiguration configuration)
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
    /// The selected engine plugin. Throws when the configured Type has no registered plugin
    /// (the unknown-engine failure mode).
    /// </summary>
    public IDatabaseEngineBuilder Engine() =>
        _engine ??= _services.GetKeyedService<IDatabaseEngineBuilder>(NormalizeType(_type))
            ?? throw new InvalidOperationException(
                $"{TypeKey} '{_type}' has no registered database engine plugin. Register its " +
                "implementation (e.g. AddGertDatabaseSqlite) in the composition root. Use 'Sqlite'.");
}
