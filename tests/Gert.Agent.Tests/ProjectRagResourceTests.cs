using System.Text;
using FluentAssertions;
using Gert.Agent.Hosting;
using Gert.Chat;
using Gert.Model;
using Gert.Model.Rag;
using Gert.Rag;
using Gert.Testing.Fakes;
using Gert.Tools;
using Gert.Tools.Resources;
using NSubstitute;
using Xunit;

namespace Gert.Agent.Tests;

/// <summary>
/// <see cref="ProjectRagResource"/> (chat-and-tools.md section RAG / hybrid retrieval): the
/// host-owned RAG seam that embeds the query, opens THIS project's rag.db for the pre-scoped
/// identity, runs the hybrid search, and maps each <see cref="RetrievedChunk"/> onto the
/// contracts-level <see cref="RagSearchHit"/> (decoding the base64 filename into the title).
/// These cover the behaviour that moved out of the old RagTool.
/// </summary>
public sealed class ProjectRagResourceTests
{
    private const string Iss = "https://issuer.test";
    private const string Sub = "user-123";
    private const string Pid = "default";

    private static IRagStore StoreReturning(params RetrievedChunk[] hits)
    {
        var store = Substitute.For<IRagStore>();
        store
            .HybridSearchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<float>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(hits);
        return store;
    }

    private static IRagIndexProvider ProviderFor(IRagStore store)
    {
        var provider = Substitute.For<IRagIndexProvider>();
        provider
            .OpenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(store);
        return provider;
    }

    private static RetrievedChunk Chunk(string docId, string filename, string? page, string content, double score) => new()
    {
        Chunk = new Chunk { Id = 1, DocumentId = docId, Ordinal = 0, Content = content, Page = page },
        Document = new Document
        {
            Id = docId,
            Filename = filename,
            Mime = "application/pdf",
            SizeBytes = 1024,
            Status = DocumentStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow,
        },
        Score = score,
    };

    [Fact]
    public async Task Embeds_opens_the_scoped_index_then_maps_the_hits()
    {
        var store = StoreReturning(
            Chunk("doc-1", Convert.ToBase64String(Encoding.UTF8.GetBytes("Favorite database.pdf")), "p.4", "sqlite-vec wins", 0.89));
        var provider = ProviderFor(store);

        var resource = new ProjectRagResource(provider, new FakeEmbeddings(), Iss, Sub, Pid);
        var hits = await resource.SearchAsync(RagSearchScope.Project, "qdrant", 5, CancellationToken.None);

        // rag.db is opened for the pre-scoped identity + pid, never caller-supplied.
        await provider.Received(1).OpenAsync(Iss, Sub, Pid, Arg.Any<CancellationToken>());

        // The query was embedded (1024-dim vector) and passed through with k.
        await store.Received(1).HybridSearchAsync(
            "qdrant",
            Arg.Is<IReadOnlyList<float>>(v => v.Count == FakeEmbeddings.Dimensions),
            5,
            Arg.Any<CancellationToken>());

        var hit = hits.Single();
        hit.DocId.Should().Be("doc-1");
        hit.Kind.Should().Be("document");
        hit.Page.Should().Be("p.4");
        hit.Score.Should().Be(0.89);
        hit.Content.Should().Be("sqlite-vec wins");
        // The base64 filename is decoded into the title (StoredFilenames).
        hit.Title.Should().Be("Favorite database.pdf");
    }

    [Fact]
    public async Task Throws_when_the_embedding_client_returns_no_vector()
    {
        // Contract violation: one vector per input text. The throw propagates to the
        // loop's per-call catch, which surfaces the message to the model - never a
        // silent BM25-only degrade with an empty vector.
        var store = StoreReturning();
        var provider = ProviderFor(store);
        var embeddings = Substitute.For<IEmbeddingClient>();
        embeddings
            .EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<float[]>());

        var resource = new ProjectRagResource(provider, embeddings, Iss, Sub, Pid);

        var act = () => resource.SearchAsync(RagSearchScope.Project, "qdrant", 8, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*embedding*");
        await store.DidNotReceive().HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<float>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_non_project_scope()
    {
        var resource = new ProjectRagResource(
            ProviderFor(StoreReturning()), new FakeEmbeddings(), Iss, Sub, Pid);

        // No Chat scope kind exists yet, so forge one to exercise the guard.
        var unknown = new RagSearchScope((RagSearchScopeKind)999);

        var act = () => resource.SearchAsync(unknown, "q", 8, CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
