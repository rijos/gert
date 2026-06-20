using Gert.Tools;

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

    /// <inheritdoc />
    public IToolUi? Ui { get; set; }

    /// <inheritdoc />
    public IToolDelegate Delegate { get; set; } = new NoOpDelegate();

    /// <inheritdoc />
    public ToolLimits Limits { get; set; } = new(Deadline: null, TokenBudget: null);

    /// <inheritdoc />
    public IToolResources Resources => new Bundle(ObjectStore, RagIndex);

    private sealed record Bundle(IObjectResource Objects, IRagResource Rag) : IToolResources;

    private sealed class NoOpDelegate : IToolDelegate;

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

        public Task<IReadOnlyList<RagSearchHit>> SearchAsync(
            RagSearchScope scope,
            string query,
            int k,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RagSearchHit>>(Hits.Take(k).ToList());
    }
}
