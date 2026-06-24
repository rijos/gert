using System.Text;
using FluentAssertions;
using Gert.Agent.Hosting;
using Gert.Model.Rag;
using Gert.Rag;
using Gert.Service.Documents;
using Gert.Storage;
using Gert.Testing.Fakes;
using NSubstitute;
using Xunit;

namespace Gert.Agent.Tests;

/// <summary>
/// <see cref="ProjectDocumentResource"/> (chat-and-tools.md section read_document): the host-owned
/// seam behind read_document that lists a project's ready documents and returns one document's full
/// text by reading the original stored blob (files/{doc-id}) and decoding it as UTF-8. Identity is
/// pre-scoped; the blob is read under ObjectScope.Project for that identity.
/// </summary>
public sealed class ProjectDocumentResourceTests
{
    private const string Iss = "https://issuer.test";
    private const string Sub = "user-123";
    private const string Pid = "default";

    private static string Title(string name) => Convert.ToBase64String(Encoding.UTF8.GetBytes(name));

    private static Document Doc(string id, string filename, DocumentStatus status, DateTimeOffset created) => new()
    {
        Id = id,
        Filename = Title(filename),
        Mime = "text/plain",
        SizeBytes = 10,
        Status = status,
        CreatedAt = created,
    };

    private static (ProjectDocumentResource Resource, FakeObjectStore Objects) Build(params Document[] documents)
    {
        var store = Substitute.For<IRagStore>();
        store.ListDocumentsAsync(Arg.Any<CancellationToken>()).Returns(documents);
        var provider = Substitute.For<IRagIndexProvider>();
        provider.OpenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(store);
        var objects = new FakeObjectStore();
        return (new ProjectDocumentResource(provider, objects, Iss, Sub, Pid), objects);
    }

    private static void Seed(FakeObjectStore objects, string docId, byte[] bytes) =>
        objects.Seed(ObjectScope.Project(Iss, Sub, Pid), $"files/{docId}", bytes);

    [Fact]
    public async Task ListAsync_returns_ready_documents_newest_first_with_decoded_titles()
    {
        var (resource, _) = Build(
            Doc("d1", "old.json", DocumentStatus.Ready, DateTimeOffset.UtcNow.AddHours(-2)),
            Doc("d2", "new.csv", DocumentStatus.Ready, DateTimeOffset.UtcNow),
            Doc("d3", "pending.txt", DocumentStatus.Processing, DateTimeOffset.UtcNow));

        var docs = await resource.ListAsync(CancellationToken.None);

        docs.Select(d => d.Title).Should().Equal("new.csv", "old.json");
    }

    [Fact]
    public async Task ReadAsync_returns_full_text_by_exact_title()
    {
        var (resource, objects) = Build(Doc("d1", "data.json", DocumentStatus.Ready, DateTimeOffset.UtcNow));
        Seed(objects, "d1", Encoding.UTF8.GetBytes("{\"hello\":\"world\"}"));

        var content = await resource.ReadAsync("data.json", 0, 1000, CancellationToken.None);

        content.Should().NotBeNull();
        content!.IsText.Should().BeTrue();
        content.Title.Should().Be("data.json");
        content.Content.Should().Be("{\"hello\":\"world\"}");
        content.TotalChars.Should().Be(17);
        content.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task ReadAsync_pages_with_offset_and_maxChars()
    {
        var (resource, objects) = Build(Doc("d1", "big.txt", DocumentStatus.Ready, DateTimeOffset.UtcNow));
        Seed(objects, "d1", Encoding.UTF8.GetBytes("ABCDEFGHIJ"));

        var first = await resource.ReadAsync("big.txt", 0, 4, CancellationToken.None);
        first!.Content.Should().Be("ABCD");
        first.Offset.Should().Be(0);
        first.HasMore.Should().BeTrue();

        var next = await resource.ReadAsync("big.txt", first.Offset + first.Content.Length, 100, CancellationToken.None);
        next!.Content.Should().Be("EFGHIJ");
        next.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task ReadAsync_resolves_a_title_case_insensitively()
    {
        var (resource, objects) = Build(Doc("d1", "Report.MD", DocumentStatus.Ready, DateTimeOffset.UtcNow));
        Seed(objects, "d1", Encoding.UTF8.GetBytes("# title"));

        var content = await resource.ReadAsync("report.md", 0, 100, CancellationToken.None);

        content.Should().NotBeNull();
        content!.Content.Should().Be("# title");
    }

    [Fact]
    public async Task ReadAsync_returns_null_for_an_unknown_reference()
    {
        var (resource, _) = Build(Doc("d1", "data.json", DocumentStatus.Ready, DateTimeOffset.UtcNow));

        var content = await resource.ReadAsync("nope.json", 0, 100, CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_reports_binary_for_non_text_bytes()
    {
        var (resource, objects) = Build(Doc("d1", "image.bin", DocumentStatus.Ready, DateTimeOffset.UtcNow));
        Seed(objects, "d1", [0x00, 0x01, 0x02, 0x00]); // NUL bytes -> not text

        var content = await resource.ReadAsync("image.bin", 0, 100, CancellationToken.None);

        content.Should().NotBeNull();
        content!.IsText.Should().BeFalse();
        content.Content.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_reports_binary_when_the_blob_is_missing()
    {
        // Document row exists (ready) but its blob is gone (e.g. half-deleted): no throw.
        var (resource, _) = Build(Doc("d1", "gone.txt", DocumentStatus.Ready, DateTimeOffset.UtcNow));

        var content = await resource.ReadAsync("gone.txt", 0, 100, CancellationToken.None);

        content.Should().NotBeNull();
        content!.IsText.Should().BeFalse();
    }
}
