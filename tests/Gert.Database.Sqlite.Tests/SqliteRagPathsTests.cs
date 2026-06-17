using FluentAssertions;
using Gert.Rag.Sqlite;
using Gert.Storage;
using Gert.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// <see cref="SqliteRagPaths"/> effective-root resolution - the RAG engine's own
/// <c>Gert:Rag:Parameters:DataRoot</c> override, else the shared <c>Storage:DataRoot</c> -
/// mirroring the coverage <see cref="SqliteDatabasePathsTests"/> gives the database engine.
/// </summary>
public class SqliteRagPathsTests
{
    private static string Iss => ProviderFixture.ExpectedIssuer;

    [Fact]
    public void Parameters_data_root_overrides_the_shared_storage_root()
    {
        using var storageRoot = new TempDataRoot();
        using var ragRoot = new TempDataRoot();

        // The engine override wins: rag.db resolves under Gert:Rag:Parameters:DataRoot,
        // independent of the structured databases / the object store on Storage:DataRoot.
        var paths = new SqliteRagPaths(
            Options.Create(new StorageOptions { DataRoot = storageRoot.Path }),
            Options.Create(new SqliteRagParameters { DataRoot = ragRoot.Path }));

        paths.RagDb(Iss, "alice", "default").Should().StartWith(ragRoot.Path);
        paths.RagDb(Iss, "alice", "default").Should().NotContain(storageRoot.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Unconfigured_data_root_fails_fast_at_construction(string dataRoot)
    {
        // Neither an engine override nor a shared root: fail fast, never resolve ./users.
        var act = () => new SqliteRagPaths(
            Options.Create(new StorageOptions { DataRoot = dataRoot }),
            Options.Create(new SqliteRagParameters()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*DataRoot*");
    }

    [Fact]
    public void User_rag_files_by_key_rejects_an_out_of_shape_key()
    {
        using var root = new TempDataRoot();
        var paths = ProviderFixture.RagPathsFor(root);

        // The admin path validates the folder key (security F6) before any path is formed.
        var act = () => paths.UserRagDatabaseFilesByKey("../alice");

        act.Should().Throw<ArgumentException>();
    }
}
