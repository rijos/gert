using Gert.Storage;
using Microsoft.Extensions.Options;

namespace Gert.Rag.Sqlite;

/// <summary>
/// The <c>Sqlite</c> RAG-engine plugin (<see cref="IRagEngineBuilder"/>): builds the
/// per-project sqlite-vec + FTS5 index provider over the engine's resolved
/// <see cref="SqliteRagPaths"/> (<see cref="SqliteRagParameters.DataRoot"/> or the shared
/// <see cref="StorageOptions.DataRoot"/>) + the RAG connection factory. Registered keyed by its
/// <see cref="Type"/> in <c>AddGertRagSqlite</c>; the generic <see cref="RagEngineFactory"/>
/// resolves it when <c>Gert:Rag:Type</c> is <c>Sqlite</c> (the default) - no central switch over Type.
/// </summary>
public sealed class SqliteRagEngineBuilder : IRagEngineBuilder
{
    private readonly SqliteRagPaths _paths;
    private readonly SqliteRagConnectionFactory _factory;

    public SqliteRagEngineBuilder(
        IOptions<StorageOptions> storage,
        IOptions<SqliteRagParameters> parameters,
        SqliteRagConnectionFactory factory)
    {
        _paths = new SqliteRagPaths(storage, parameters);
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public string Type => "Sqlite";

    /// <inheritdoc />
    public IRagIndexProvider BuildRagIndexProvider() =>
        new SqliteRagIndexProvider(_paths, _factory);
}
