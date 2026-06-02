using Gert.Database.Sqlite;
using Gert.Testing;
using Microsoft.Extensions.Options;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// Shared helpers to spin a <see cref="SqliteDatabaseProvider"/> and
/// <see cref="UserPaths"/> over a throwaway <see cref="TempDataRoot"/> with a
/// fixed expected issuer.
/// </summary>
internal static class ProviderFixture
{
    public const string ExpectedIssuer = "https://id.test.local";

    public static StorageOptions OptionsFor(TempDataRoot root, string? issuer = null) => new()
    {
        DataRoot = root.Path,
        ExpectedIssuer = issuer ?? ExpectedIssuer,
    };

    public static SqliteDatabaseProvider ProviderFor(TempDataRoot root, string? issuer = null) =>
        new(Options.Create(OptionsFor(root, issuer)));

    public static UserPaths PathsFor(TempDataRoot root, string? issuer = null) =>
        new(Options.Create(OptionsFor(root, issuer)));
}
