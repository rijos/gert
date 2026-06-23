using System.Reflection;
using FluentAssertions;
using Gert.Chat.OpenAI;
using Gert.Database;
using Gert.Database.Sqlite;
using Gert.Model.Plugins;
using Gert.Rag;
using Gert.Rag.Sqlite;
using Gert.Tools.Sandbox;
using Gert.Tools.Search;
using Xunit;

namespace Gert.Chat.Tests;

/// <summary>
/// The ENFORCEABLE contracts-vs-impl split + naming convention that the capability-plugin
/// pattern guarantees (tech-stack.md section Architecture): for every keyed capability, the
/// generic factory/ports live in the contracts location and must not hold a concrete plugin;
/// each <see cref="ICapabilityPlugin"/> lives in its per-impl leaf; and every capability-impl DI
/// registrar follows <c>AddGert&lt;Capability&gt;&lt;Impl&gt;</c>. A capability may be split by
/// ASSEMBLY (chat: <c>Gert.Chat</c> contracts vs <c>Gert.Chat.OpenAI</c> impl) or, when the ports
/// live in the shared contracts assembly (<c>Gert.Tools</c>) and the impl adapter is a single
/// multi-backend bag, by NAMESPACE leaf within one assembly (search/sandbox in
/// <c>Gert.Tools.Builtin</c>); both shapes are enforced here. As capabilities split, add a row to
/// <see cref="Capabilities"/>.
/// </summary>
public sealed class PluginArchitectureTests
{
    /// <summary>
    /// One row per keyed capability: its plugin interface, predicates locating the generic
    /// CONTRACTS code (where a plugin must NOT live) and the impl LEAF (where it must), the
    /// expected <c>AddGert&lt;Capability&gt;&lt;Impl&gt;</c> registrars, and - for an
    /// assembly-split capability - the (contracts, impl) assembly pair whose dependency direction
    /// is also enforced.
    /// </summary>
    private static readonly Capability[] Capabilities =
    [
        // Chat: split by ASSEMBLY. Contracts = Gert.Chat, impl leaf = Gert.Chat.OpenAI.
        new(
            Name: "Chat",
            PluginInterface: typeof(IChatModelClientBuilder),
            IsContracts: t => t.Assembly == typeof(IChatModelClientBuilder).Assembly,
            IsImplLeaf: t => t.Assembly == typeof(OpenAIChatModelClientBuilder).Assembly,
            Registrars: [("AddGertChatOpenAI", typeof(OpenAIChatModelClientBuilder).Assembly)],
            AssemblySplit: (typeof(IChatModelClientBuilder).Assembly, typeof(OpenAIChatModelClientBuilder).Assembly)),

        // Database engine: split by ASSEMBLY. Contracts = Gert.Database, impl leaf = Gert.Database.Sqlite.
        new(
            Name: "Database",
            PluginInterface: typeof(IDatabaseEngineBuilder),
            IsContracts: t => t.Assembly == typeof(IDatabaseEngineBuilder).Assembly,
            IsImplLeaf: t => t.Assembly == typeof(SqliteDatabaseEngineBuilder).Assembly,
            Registrars: [("AddGertDatabaseSqlite", typeof(SqliteDatabaseEngineBuilder).Assembly)],
            AssemblySplit: (typeof(IDatabaseEngineBuilder).Assembly, typeof(SqliteDatabaseEngineBuilder).Assembly)),

        // RAG engine: split by ASSEMBLY. Contracts = Gert.Rag, impl leaf = Gert.Rag.Sqlite.
        new(
            Name: "Rag",
            PluginInterface: typeof(IRagEngineBuilder),
            IsContracts: t => t.Assembly == typeof(IRagEngineBuilder).Assembly,
            IsImplLeaf: t => t.Assembly == typeof(SqliteRagEngineBuilder).Assembly,
            Registrars: [("AddGertRagSqlite", typeof(SqliteRagEngineBuilder).Assembly)],
            AssemblySplit: (typeof(IRagEngineBuilder).Assembly, typeof(SqliteRagEngineBuilder).Assembly)),

        // Search: split by NAMESPACE leaf inside Gert.Tools.Builtin (the ports live in the
        // Gert.Tools contracts assembly, so the impl leaf is a single multi-backend bag).
        new(
            Name: "Search",
            PluginInterface: typeof(IWebSearchBuilder),
            IsContracts: t => t.Namespace == "Gert.Tools.Search",
            IsImplLeaf: t => t.Namespace?.StartsWith("Gert.Tools.Search.", StringComparison.Ordinal) == true,
            Registrars: [("AddGertSearchSearXNG", typeof(IWebSearchBuilder).Assembly)],
            AssemblySplit: null),

        // Sandbox: split by NAMESPACE leaf inside Gert.Tools.Builtin, two impls (Monty, GVisor).
        new(
            Name: "Sandbox",
            PluginInterface: typeof(IPythonSandboxBuilder),
            IsContracts: t => t.Namespace == "Gert.Tools.Sandbox",
            IsImplLeaf: t => t.Namespace?.StartsWith("Gert.Tools.Sandbox.", StringComparison.Ordinal) == true,
            Registrars:
            [
                ("AddGertSandboxMonty", typeof(IPythonSandboxBuilder).Assembly),
                ("AddGertSandboxGVisor", typeof(IPythonSandboxBuilder).Assembly),
            ],
            AssemblySplit: null),
    ];

    /// <summary>The assemblies that may declare any capability plugin or its registrar.</summary>
    private static readonly Assembly[] ScannedAssemblies =
    [
        typeof(IChatModelClientBuilder).Assembly,
        typeof(OpenAIChatModelClientBuilder).Assembly,
        typeof(IWebSearchBuilder).Assembly,
        typeof(IDatabaseEngineBuilder).Assembly,
        typeof(SqliteDatabaseEngineBuilder).Assembly,
        typeof(IRagEngineBuilder).Assembly,
        typeof(SqliteRagEngineBuilder).Assembly,
    ];

    private static IEnumerable<Type> PluginsFor(Capability capability) =>
        ScannedAssemblies.Distinct().SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && capability.PluginInterface.IsAssignableFrom(t));

    [Fact]
    public void Capability_plugins_live_in_the_impl_leaf_not_the_contracts_location()
    {
        foreach (var capability in Capabilities)
        {
            PluginsFor(capability).Where(capability.IsContracts).Select(t => t.FullName)
                .Should().BeEmpty(
                    $"{capability.Name} ICapabilityPlugin implementations must live in the impl " +
                    "leaf, never in the contracts location (the generic factory/ports)");
        }
    }

    [Fact]
    public void Each_capability_registers_at_least_one_plugin_in_its_impl_leaf()
    {
        foreach (var capability in Capabilities)
        {
            PluginsFor(capability).Where(capability.IsImplLeaf)
                .Should().NotBeEmpty(
                    $"{capability.Name} should implement at least one plugin in its impl leaf");
        }
    }

    [Fact]
    public void Every_plugin_is_in_an_impl_leaf()
    {
        // No plugin escapes the leaf into some other namespace/assembly the split does not cover.
        foreach (var capability in Capabilities)
        {
            PluginsFor(capability).Where(t => !capability.IsImplLeaf(t)).Select(t => t.FullName)
                .Should().BeEmpty($"every {capability.Name} plugin must live in its impl leaf");
        }
    }

    [Fact]
    public void Assembly_split_contracts_do_not_depend_on_their_impl_leaf()
    {
        foreach (var capability in Capabilities.Where(c => c.AssemblySplit is not null))
        {
            var (contracts, impl) = capability.AssemblySplit!.Value;
            var implName = impl.GetName().Name;
            contracts.GetReferencedAssemblies().Select(a => a.Name)
                .Should().NotContain(
                    implName,
                    $"{contracts.GetName().Name} is a capability contracts assembly and must not " +
                    $"reference its implementation '{implName}'");
        }
    }

    [Fact]
    public void Assembly_split_impl_leaves_do_not_depend_on_the_service_layer()
    {
        // An impl leaf reaches the world through its contracts ports, never the business-logic
        // layer (tech-stack.md section Architecture): it references only its contracts + Gert.Model
        // (+ Gert.Storage for the file-backed engines). This guards against re-introducing an
        // outward edge like the removed Gert.Database.Sqlite -> Gert.Service reference.
        foreach (var capability in Capabilities.Where(c => c.AssemblySplit is not null))
        {
            var impl = capability.AssemblySplit!.Value.Impl;
            impl.GetReferencedAssemblies().Select(a => a.Name)
                .Should().NotContain(
                    "Gert.Service",
                    $"the {capability.Name} impl leaf '{impl.GetName().Name}' must not reference the " +
                    "service layer");
        }
    }

    [Fact]
    public void Every_capability_impl_registrar_follows_the_AddGert_Capability_Impl_convention()
    {
        foreach (var capability in Capabilities)
        {
            var prefix = $"AddGert{capability.Name}";
            foreach (var (method, assembly) in capability.Registrars)
            {
                HasPublicStaticMethod(assembly, method)
                    .Should().BeTrue(
                        $"the {capability.Name} impl registrar '{method}' must exist as a public " +
                        $"static method in '{assembly.GetName().Name}'");

                method.Should().StartWith(prefix, $"'{method}' must follow AddGert{capability.Name}<Impl>");
                method.Length.Should().BeGreaterThan(prefix.Length, $"'{method}' needs an <Impl> suffix");
                char.IsUpper(method[prefix.Length])
                    .Should().BeTrue($"the <Impl> suffix of '{method}' must start with an uppercase letter");
            }
        }
    }

    private static bool HasPublicStaticMethod(Assembly assembly, string name) =>
        assembly.GetTypes().Any(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Any(m => m.Name == name));

    /// <summary>One keyed capability's contracts-vs-impl shape (see <see cref="Capabilities"/>).</summary>
    private sealed record Capability(
        string Name,
        Type PluginInterface,
        Func<Type, bool> IsContracts,
        Func<Type, bool> IsImplLeaf,
        (string Method, Assembly Assembly)[] Registrars,
        (Assembly Contracts, Assembly Impl)? AssemblySplit);
}
