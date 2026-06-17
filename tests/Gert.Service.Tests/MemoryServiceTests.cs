using System.Text;
using FluentAssertions;
using Gert.Chat;
using Gert.Model;
using Gert.Model.Rag;
using Gert.Rag;
using Gert.Service.Documents;
using Gert.Service.External;
using Gert.Service.Validation;
using Gert.Storage;
using NSubstitute;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// Unit tests for <see cref="MemoryService.ListAsync"/> - the list orders by
/// worth (pinned first, then newest); the repository's row order is
/// storage-incidental and must never leak to the API.
/// </summary>
public sealed class MemoryServiceTests
{
    private static Document Doc(string id, bool pinned, int daysAgo) => new()
    {
        Id = id,
        Filename = Convert.ToBase64String(Encoding.UTF8.GetBytes(id)),
        Mime = "text/markdown",
        SizeBytes = 1,
        Status = DocumentStatus.Ready,
        Kind = DocumentKind.Memory,
        Pinned = pinned,
        CreatedAt = DateTimeOffset.UnixEpoch.AddDays(1000 - daysAgo),
    };

    [Fact]
    public async Task List_orders_pinned_first_then_newest()
    {
        var repo = Substitute.For<IRagStore>();
        repo.ListDocumentsAsync(DocumentKind.Memory, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Doc("old-unpinned", pinned: false, daysAgo: 9),
                Doc("new-unpinned", pinned: false, daysAgo: 1),
                Doc("old-pinned", pinned: true, daysAgo: 30),
                Doc("new-pinned", pinned: true, daysAgo: 2),
            });

        var provider = Substitute.For<IRagIndexProvider>();
        provider.OpenAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(repo);

        var user = Substitute.For<IUserContext>();
        user.Iss.Returns("https://idp.test");
        user.Sub.Returns("user-1");

        var service = new MemoryService(
            provider,
            Substitute.For<IObjectStore>(),
            Substitute.For<IEmbeddingClient>(),
            Substitute.For<IValidationProvider>(),
            user,
            TimeProvider.System);

        var entries = await service.ListAsync("default");

        entries.Select(e => e.Id).Should().Equal(
            "new-pinned", "old-pinned", "new-unpinned", "old-unpinned");
    }
}
