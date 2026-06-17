using Gert.Model.Plugins;

namespace Gert.Database;

/// <summary>
/// A database-engine plugin: builds the per-database providers for one engine
/// <see cref="ICapabilityPlugin.Type"/> (e.g. <c>Sqlite</c>). Each implementation
/// assembly registers exactly one, keyed by its Type; the generic
/// <see cref="DatabaseEngineFactory"/> resolves the builder for the configured
/// <c>Gert:Database:Type</c> and delegates - there is no central <c>switch</c> over Type
/// (mirrors <c>Gert.Chat.IChatModelClientBuilder</c> / <c>Gert.Tools.IPythonSandboxBuilder</c>).
/// The builder owns its engine's connection mechanics (db-file paths for a file-backed
/// engine, a connection string for a server engine), so the contracts assembly stays
/// engine-agnostic.
/// </summary>
public interface IDatabaseEngineBuilder : ICapabilityPlugin
{
    /// <summary>Build the <see cref="IUserDatabaseProvider"/> for this engine.</summary>
    IUserDatabaseProvider BuildUserDatabaseProvider();

    /// <summary>Build the <see cref="IChatDatabaseProvider"/> for this engine.</summary>
    IChatDatabaseProvider BuildChatDatabaseProvider();
}
