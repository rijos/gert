using Gert.Model.Chat;
using Gert.Tools;
using Gert.Tools.Hosting;
using Gert.Tools.Resources;
using Gert.Tools.Ui;

namespace Gert.Testing.Fakes;

/// <summary>
/// A configurable in-memory <see cref="IToolHost"/> for tool tests: an in-memory object store, a
/// scripted RAG index, a settable <see cref="Ui"/> (null = autonomous host) and
/// <see cref="Delegate"/>, and adjustable <see cref="Limits"/>. <see cref="Shared"/> is a
/// do-nothing default for tools that ignore the host; tests exercising a capability configure
/// their own instance and pass it to the three-argument <see cref="ITool.ExecuteAsync"/>.
/// </summary>
public sealed class FakeToolHost : IToolHost
{
    /// <summary>A do-nothing host for tools that do not touch the capability surface.</summary>
    public static FakeToolHost Shared { get; } = new();

    /// <summary>The in-memory object store backing <see cref="Resources"/>.Objects.</summary>
    public InMemoryObjectResource ObjectStore { get; } = new();

    /// <summary>The scripted RAG index backing <see cref="Resources"/>.Rag.</summary>
    public ScriptedRagResource RagIndex { get; } = new();

    /// <summary>The scripted documents backing <see cref="Resources"/>.Documents (read_document).</summary>
    public ScriptedDocumentResource Documents { get; } = new();

    /// <inheritdoc />
    public IToolUi? Ui { get; set; }

    /// <inheritdoc />
    public IToolDelegate Delegate { get; set; } = new NoOpDelegate();

    /// <inheritdoc />
    public ToolLimits Limits { get; set; } = new(Deadline: null, TokenBudget: null);

    /// <summary>Captures the side-effects a tool reports (citations/artifacts/stdout/todos) for assertions.</summary>
    public CapturingToolCard Captured { get; } = new();

    /// <inheritdoc />
    public IToolCard Card => Captured;

    /// <inheritdoc />
    public IToolResources Resources => new Bundle(ObjectStore, RagIndex, Documents);

    private sealed record Bundle(IObjectResource Objects, IRagResource Rag, IDocumentResource Documents) : IToolResources;

    /// <summary>An <see cref="IToolCard"/> that records what a tool reports, for test assertions.</summary>
    public sealed class CapturingToolCard : IToolCard
    {
        private readonly List<Citation> _citations = [];
        private readonly List<Artifact> _artifacts = [];

        public IReadOnlyList<Citation> Citations => _citations;

        public IReadOnlyList<Artifact> Artifacts => _artifacts;

        public string? Stdout { get; private set; }

        public IReadOnlyList<TodoItem>? Todos { get; private set; }

        public void ReportCitations(IReadOnlyList<Citation> citations) => _citations.AddRange(citations);

        public void ReportArtifact(Artifact artifact) => _artifacts.Add(artifact);

        public void ReportStdout(string stdout) => Stdout = stdout;

        public void ReportTodos(IReadOnlyList<TodoItem> todos) => Todos = todos;
    }

    private sealed class NoOpDelegate : IToolDelegate
    {
        public Task<DelegateResult> RunAsync(DelegateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DelegateResult { Success = false, Error = "no delegate configured" });
    }

    /// <summary>A dictionary-backed <see cref="IObjectResource"/> that versions on overwrite.</summary>
    public sealed class InMemoryObjectResource : IObjectResource
    {
        private readonly Dictionary<(ResourceScope Scope, string Name), StoredObject> _store = new();

        public Task<StoredObject?> GetAsync(ResourceScope scope, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_store.GetValueOrDefault((scope, name)));

        public Task<IReadOnlyList<ObjectSummary>> ListAsync(ResourceScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ObjectSummary>>(_store
                .Where(kv => kv.Key.Scope == scope)
                .Select(kv => new ObjectSummary
                {
                    Id = kv.Value.Id,
                    Name = kv.Value.Name,
                    Version = kv.Value.Version,
                    Kind = kv.Value.Kind,
                    CreatedAt = kv.Value.CreatedAt,
                    UpdatedAt = kv.Value.UpdatedAt,
                })
                .ToList());

        public Task<StoredObject> PutAsync(ResourceScope scope, ObjectWrite write, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var existing = _store.GetValueOrDefault((scope, write.Name));
            var stored = new StoredObject
            {
                // First put assigns a stable id; an overwrite preserves the existing one.
                Id = existing?.Id ?? Guid.NewGuid().ToString("D"),
                Name = write.Name,
                Content = write.Content,
                Kind = write.Kind,
                Version = (existing?.Version ?? 0) + 1,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now,
            };
            _store[(scope, write.Name)] = stored;
            return Task.FromResult(stored);
        }

        public Task<bool> DeleteAsync(ResourceScope scope, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_store.Remove((scope, name)));
    }

    /// <summary>An <see cref="IRagResource"/> that returns the first k of a settable hit list.</summary>
    public sealed class ScriptedRagResource : IRagResource
    {
        /// <summary>The hits returned (truncated to k), in rank order. Tests append to script results.</summary>
        public List<RagSearchHit> Hits { get; } = [];

        /// <summary>How many times <see cref="SearchAsync"/> ran - asserts fail-closed paths never search.</summary>
        public int Searches { get; private set; }

        public Task<IReadOnlyList<RagSearchHit>> SearchAsync(
            RagSearchScope scope,
            string query,
            int k,
            CancellationToken cancellationToken = default)
        {
            Searches++;
            return Task.FromResult<IReadOnlyList<RagSearchHit>>(Hits.Take(k).ToList());
        }
    }

    /// <summary>
    /// An <see cref="IDocumentResource"/> backed by a settable name -> full-text map: lists those
    /// names and serves paged slices of the matching text (read_document). An unknown reference
    /// returns null (the tool then lists the candidates).
    /// </summary>
    public sealed class ScriptedDocumentResource : IDocumentResource
    {
        /// <summary>Title -> full text. Tests seed this; binary docs are modelled by seeding via <see cref="Binary"/>.</summary>
        public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

        /// <summary>Titles whose read should report a binary (non-text) document.</summary>
        public HashSet<string> Binary { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<DocumentSummary> docs = Texts.Keys.Concat(Binary)
                .Distinct(StringComparer.Ordinal)
                .Select(name => new DocumentSummary
                {
                    Id = name,
                    Title = name,
                    Mime = "text/plain",
                    SizeBytes = Texts.TryGetValue(name, out var t) ? t.Length : 0,
                })
                .ToList();
            return Task.FromResult(docs);
        }

        public Task<DocumentContent?> ReadAsync(
            string docRef, int offset, int maxChars, CancellationToken cancellationToken = default)
        {
            if (Binary.Contains(docRef))
            {
                return Task.FromResult<DocumentContent?>(new DocumentContent
                {
                    Title = docRef, IsText = false, Content = string.Empty,
                    TotalChars = 0, Offset = 0, HasMore = false,
                });
            }

            if (!Texts.TryGetValue(docRef, out var text))
            {
                return Task.FromResult<DocumentContent?>(null);
            }

            var from = Math.Clamp(offset, 0, text.Length);
            var take = Math.Clamp(maxChars, 0, text.Length - from);
            return Task.FromResult<DocumentContent?>(new DocumentContent
            {
                Title = docRef,
                IsText = true,
                Content = text.Substring(from, take),
                TotalChars = text.Length,
                Offset = from,
                HasMore = from + take < text.Length,
            });
        }
    }
}
