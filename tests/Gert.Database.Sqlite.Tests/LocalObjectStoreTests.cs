using System.Text;
using FluentAssertions;
using Gert.Storage;
using Gert.Storage.Local;
using Gert.Testing;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// Round-trips <see cref="LocalObjectStore"/> against a throwaway
/// <see cref="TempDataRoot"/> - put/read/exists/delete, prefix-delete/list, and the
/// path-traversal guard.
/// </summary>
public class LocalObjectStoreTests
{
    private const string Sub = "alice-sub";

    private static ObjectScope Scope() =>
        ObjectScope.Project(ProviderFixture.ExpectedIssuer, Sub, "default");

    private static async Task<LocalObjectStore> NewStoreAsync(TempDataRoot root)
    {
        // Provision so the project scope exists (mirrors real usage).
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);
        return ProviderFixture.ObjectsFor(root);
    }

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
    }

    [Fact]
    public async Task Put_read_exists_delete_round_trip()
    {
        await using var root = new TempDataRoot();
        var store = await NewStoreAsync(root);
        var scope = Scope();

        (await store.ExistsAsync(scope, "upload.pdf")).Should().BeFalse();

        await store.PutAsync(scope, "upload.pdf", Bytes("hello blob"));

        (await store.ExistsAsync(scope, "upload.pdf")).Should().BeTrue();
        (await ReadAllAsync(await store.OpenReadAsync(scope, "upload.pdf"))).Should().Be("hello blob");

        (await store.DeleteAsync(scope, "upload.pdf")).Should().BeTrue();
        (await store.ExistsAsync(scope, "upload.pdf")).Should().BeFalse();
        // Deleting a missing blob is idempotent (no throw, returns false).
        (await store.DeleteAsync(scope, "upload.pdf")).Should().BeFalse();
    }

    [Fact]
    public async Task Put_overwrites_and_creates_nested_directories()
    {
        await using var root = new TempDataRoot();
        var store = await NewStoreAsync(root);
        var scope = Scope();

        await store.PutAsync(scope, "exports/decision.md", Bytes("v1"));
        await store.PutAsync(scope, "exports/decision.md", Bytes("v2"));

        (await ReadAllAsync(await store.OpenReadAsync(scope, "exports/decision.md"))).Should().Be("v2");
    }

    [Fact]
    public async Task OpenRead_missing_blob_throws_file_not_found()
    {
        await using var root = new TempDataRoot();
        var store = await NewStoreAsync(root);

        var act = async () => await store.OpenReadAsync(Scope(), "nope.bin");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task DeletePrefix_removes_matching_blobs_and_lists_the_rest()
    {
        await using var root = new TempDataRoot();
        var store = await NewStoreAsync(root);
        var scope = Scope();

        await store.PutAsync(scope, "docs/a.pdf", Bytes("a"));
        await store.PutAsync(scope, "docs/sub/b.pdf", Bytes("b"));
        await store.PutAsync(scope, "exports/c.md", Bytes("c"));

        var removed = await store.DeletePrefixAsync(scope, "docs/");

        removed.Should().Be(2);
        (await store.ExistsAsync(scope, "docs/a.pdf")).Should().BeFalse();
        (await store.ExistsAsync(scope, "docs/sub/b.pdf")).Should().BeFalse();

        // The project scope dir can also hold the db files locally; assert by prefix.
        (await store.ListAsync(scope, "exports/")).Should().Equal("exports/c.md");
        (await store.ListAsync(scope, "docs/")).Should().BeEmpty();
    }

    [Fact]
    public async Task List_returns_keys_under_prefix()
    {
        await using var root = new TempDataRoot();
        var store = await NewStoreAsync(root);
        var scope = Scope();

        await store.PutAsync(scope, "docs/a.pdf", Bytes("a"));
        await store.PutAsync(scope, "docs/b.pdf", Bytes("b"));
        await store.PutAsync(scope, "exports/c.md", Bytes("c"));

        (await store.ListAsync(scope, "docs/")).Should().Equal("docs/a.pdf", "docs/b.pdf");
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("nested/../../escape.txt")]
    public async Task Traversal_key_is_rejected(string key)
    {
        await using var root = new TempDataRoot();
        var store = await NewStoreAsync(root);

        var act = async () => await store.PutAsync(Scope(), key, Bytes("x"));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Bare_dotdot_key_naming_the_scope_parent_is_rejected()
    {
        // A key of exactly ".." resolves to the scope root's PARENT - no trailing
        // separator, so it is NOT caught by the StartsWith(root + sep) test unless the
        // guard rejects an escape to the parent itself. Belt-and-braces over traversal.
        await using var root = new TempDataRoot();
        var store = await NewStoreAsync(root);

        var act = async () => await store.PutAsync(Scope(), "..", Bytes("x"));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Key_with_a_leading_separator_is_rejected_as_rooted()
    {
        // A leading "/" makes the key rooted, so it must never be joined under the
        // scope - Path.IsPathRooted catches it before any combine.
        await using var root = new TempDataRoot();
        var store = await NewStoreAsync(root);

        var leading = OperatingSystem.IsWindows() ? @"\evil.txt" : "/evil.txt";
        var act = async () => await store.PutAsync(Scope(), leading, Bytes("x"));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Absolute_key_is_rejected()
    {
        await using var root = new TempDataRoot();
        var store = await NewStoreAsync(root);

        var absolute = OperatingSystem.IsWindows() ? @"C:\evil.txt" : "/evil.txt";
        var act = async () => await store.PutAsync(Scope(), absolute, Bytes("x"));
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
