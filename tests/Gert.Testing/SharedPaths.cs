namespace Gert.Testing;

/// <summary>
/// Resolves the <c>tests/shared/</c> contract directory (testing.md Appendix A.5)
/// at runtime by walking up from the test binary until a <c>tests/shared</c>
/// folder containing <c>fixtures.json</c> is found. This keeps both the fakes and
/// the golden-file conformance test independent of build-output layout - no
/// brittle <c>../../../</c> relative paths and no need to copy the file.
/// </summary>
public static class SharedPaths
{
    /// <summary>The resolved absolute path to <c>tests/shared/</c>.</summary>
    public static string SharedDir { get; } = Resolve();

    /// <summary>Absolute path to <c>tests/shared/fixtures.json</c>.</summary>
    public static string FixturesJson => Path.Combine(SharedDir, "fixtures.json");

    /// <summary>Absolute path to <c>tests/shared/embeddings_golden.json</c> (committed golden file).</summary>
    public static string EmbeddingsGoldenJson => Path.Combine(SharedDir, "embeddings_golden.json");

    private static string Resolve()
    {
        // Start from the test binary's directory and walk up to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "shared");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "fixtures.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate 'tests/shared' (with fixtures.json) walking up from " +
            AppContext.BaseDirectory + ". It is the shared-fake contract dir (testing.md A.5).");
    }
}
