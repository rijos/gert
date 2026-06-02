namespace Gert.Testing;

/// <summary>
/// A throwaway <c>DataRoot</c> for a test (testing.md §4.4). Creates a unique
/// directory under the OS temp dir, exposes its <see cref="Path"/>, and
/// recursively deletes it on dispose. Because a user is just a folder
/// (principles.md), pointing the host's <c>DataRoot</c> here gives the cleanest
/// isolation assertion: after a two-user test, two sibling
/// <c>sha256(iss + sub)</c> directories exist under it and neither
/// <c>rag.db</c> contains the other's chunks.
/// </summary>
public sealed class TempDataRoot : IDisposable, IAsyncDisposable
{
    /// <summary>Create and materialize a fresh temp DataRoot directory.</summary>
    public TempDataRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "gert-tests",
            "dataroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>The absolute path to this DataRoot — point the host's <c>DataRoot</c> here.</summary>
    public string Path { get; }

    /// <summary>The conventional <c>users/</c> subdirectory under this root.</summary>
    public string UsersDir => System.IO.Path.Combine(Path, "users");

    /// <inheritdoc />
    public void Dispose()
    {
        TryDelete();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TryDelete();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void TryDelete()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup: a transient lock (e.g. a not-yet-closed SQLite
            // handle on Windows) should never fail a test. The OS temp dir is reaped anyway.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale — never let cleanup throw.
        }
    }
}
