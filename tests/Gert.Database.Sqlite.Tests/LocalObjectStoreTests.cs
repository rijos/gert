using System.Text;
using FluentAssertions;
using Gert.Service.Storage;
using Gert.Testing;
using Xunit;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// Round-trips <see cref="LocalObjectStore"/> against a throwaway
/// <see cref="TempDataRoot"/> — put/read/exists/delete, prefix-delete/list, and the
/// path-traversal guard.
/// </summary>
public class LocalObjectStoreTests
{
    private const string Sub = "alice-sub";

    private static ObjectScope Scope() => new(ProviderFixture.ExpectedIssuer, Sub, "default");

    private static async Task<LocalObjectStore> NewStoreAsync(TempDataRoot root)
    {
        // Provision so the project's files/ root exists (mirrors real usage).
        var provider = ProviderFixture.ProviderFor(root);
        await provider.EnsureProvisionedAsync(ProviderFixture.ExpectedIssuer, Sub);
        return new LocalObjectStore(ProviderFixture.PathsFor(root));
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

        (await store.ListAsync(scope, string.Empty)).Should().Equal("exports/c.md");
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
    public async Task Absolute_key_is_rejected()
    {
        await using var root = new TempDataRoot();
        var store = await NewStoreAsync(root);

        var absolute = OperatingSystem.IsWindows() ? @"C:\evil.txt" : "/evil.txt";
        var act = async () => await store.PutAsync(Scope(), absolute, Bytes("x"));
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
