using System.Reflection;
using Mailcoded.Core.Protocol;
using NetArchTest.Rules;
using Xunit;

namespace Mailcoded.Core.Tests.Architecture;

/// <summary>ARCHITECTURE §12.1.4-5 and CLAUDE invariants 5 and 8: hosts adapt, they never decide.</summary>
public sealed class HostBoundaryTests
{
    private static readonly string[] MailAndSqlLibraries =
        ["MailKit", "MimeKit", "Microsoft.Data.Sqlite", "SQLitePCL"];

    private static readonly string[] BusinessLogicTypes =
    [
        "Mailcoded.Core.Domain.Sync.SyncPlanner",
        "Mailcoded.Core.Domain.Outbox.OutboxMessage",
        "Mailcoded.Core.Domain.Outbox.OutboxReconciler",
        "Mailcoded.Core.Domain.Search.SearchQueryParser",
    ];

    /// <summary>Verbs that must not name anything an agent can invoke (CLAUDE invariant 5).</summary>
    private static readonly string[] RemovalVerbs = ["delete", "expunge", "trash", "purge"];

    private static readonly string[] NonMailRemovalAllowlist =
    [
        "ConfirmTokenStore.Purge",
    ];

    [Fact]
    public void The_daemon_host_is_reachable_from_these_tests()
    {
        Assert.True(
            ArchitectureAssemblies.Daemon is not null,
            $"'{ArchitectureAssemblies.DaemonAssemblyName}' could not be loaded, so every host rule below would "
            + "pass over an empty set. Mailcoded.Core.Tests must keep its ProjectReference to Mailcoded.Daemon.");
    }

    [Fact]
    public void Hosts_never_reference_a_mail_or_sql_library_type()
    {
        AssertHosts(
            MailAndSqlLibraries,
            "RULE (CLAUDE invariant 8): hosts wire adapters together and translate wire DTOs. A MailKit, MimeKit or "
            + "Microsoft.Data.Sqlite type inside Daemon/Cli/Mcp means protocol or storage detail escaped Core, where "
            + "it can be tested. Put it behind IMailProvider, Parsing or SqliteStore and pass a Core type to the host.");
    }

    [Fact]
    public void Hosts_contain_no_business_logic()
    {
        AssertHosts(
            BusinessLogicTypes,
            "RULE (ARCHITECTURE §12.1.4, CLAUDE invariant 8): a host may CONSTRUCT an adapter in its composition root, "
            + "but it must not run a decision. Planning a sync, folding an outbox transition, deciding a reconciliation "
            + "or parsing a query belongs in Core where a table-driven test can reach it without a process.");
    }

    [Fact]
    public void No_rpc_method_is_named_after_a_mail_removal_verb()
    {
        foreach (var field in typeof(RpcMethods).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not string method) continue;

            foreach (var verb in RemovalVerbs)
            {
                Assert.False(
                    method.Contains(verb, StringComparison.OrdinalIgnoreCase),
                    $"RpcMethods exposes '{method}'. RULE (CLAUDE invariant 5): no delete, expunge, trash or purge "
                    + "operation may exist on the agent surface — ABSENT, not gated behind a flag or a confirmation "
                    + "token. An agent that cannot name the verb cannot be talked into using it. Mail leaves this "
                    + "machine only through send, and moves only through message.move.");
            }
        }
    }

    [Fact]
    public void No_type_on_the_agent_surface_is_named_after_a_mail_removal_verb()
    {
        foreach (var type in ArchitectureAssemblies.AgentSurfaceTypes())
        {
            if (!ArchitectureAssemblies.IsAuthored(type)) continue;

            foreach (var verb in RemovalVerbs)
            {
                Assert.False(
                    type.Name.Contains(verb, StringComparison.OrdinalIgnoreCase),
                    $"'{type.FullName}' names a mail-removal verb on the agent surface. RULE (CLAUDE invariant 5): "
                    + "there is no delete/expunge/trash/purge tool in the CLI or MCP surface and there never will be. "
                    + "Server-observed removal is modelled in Domain as SyncEvent.MessageExpunged — an event the "
                    + "server reports to us, not a command we can issue.");
            }
        }
    }

    [Fact]
    public void No_public_operation_on_the_agent_surface_is_named_after_a_mail_removal_verb()
    {
        const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in ArchitectureAssemblies.AgentSurfaceTypes())
        {
            if (!ArchitectureAssemblies.IsAuthored(type)) continue;

            foreach (var method in type.GetMethods(Flags))
            {
                if (method.IsSpecialName) continue;

                var qualified = $"{type.Name}.{method.Name}";
                if (NonMailRemovalAllowlist.Contains(qualified, StringComparer.Ordinal)) continue;

                foreach (var verb in RemovalVerbs)
                {
                    Assert.False(
                        method.Name.Contains(verb, StringComparison.OrdinalIgnoreCase),
                        $"'{type.FullName}.{method.Name}' is callable from the agent surface. RULE (CLAUDE "
                        + "invariant 5): no operation that removes mail may be reachable from the CLI or MCP. If the "
                        + "method removes something that is not mail (a cached token, a temp file), add it to "
                        + $"{nameof(NonMailRemovalAllowlist)} with a reason — do not rename it to slip past this test.");
                }
            }
        }
    }

    [Fact]
    public void The_removal_allowlist_only_names_things_that_still_exist()
    {
        var surface = ArchitectureAssemblies.AgentSurfaceTypes();

        foreach (var entry in NonMailRemovalAllowlist)
        {
            var parts = entry.Split('.');
            Assert.Equal(2, parts.Length);

            var found = surface.Any(t =>
                string.Equals(t.Name, parts[0], StringComparison.Ordinal)
                && t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Any(m => string.Equals(m.Name, parts[1], StringComparison.Ordinal)));

            Assert.True(
                found,
                $"The removal allowlist still exempts '{entry}', which no longer exists. A stale exemption is a hole: "
                + "delete the entry so the rule covers the surface again.");
        }
    }

    private static void AssertHosts(string[] forbidden, string rule)
    {
        var hosts = ArchitectureAssemblies.ReachableHosts;
        Assert.NotEmpty(hosts);

        foreach (var host in hosts)
        {
            var scope = Types.InAssembly(host).That().ResideInNamespace("Mailcoded");
            Assert.True(scope.GetTypes().Any(), $"'{host.GetName().Name}' has no types under 'Mailcoded'.");

            var result = Types.InAssembly(host)
                .That().ResideInNamespace("Mailcoded")
                .ShouldNot().HaveDependencyOnAny(forbidden)
                .GetResult();

            var failing = result.FailingTypes ?? Enumerable.Empty<Type>();
            var names = failing.Select(t => t.FullName ?? t.Name).ToArray();

            Assert.True(
                result.IsSuccessful,
                $"{rule}{Environment.NewLine}Host '{host.GetName().Name}' offends via: "
                + (names.Length == 0 ? "(none reported)" : string.Join(", ", names)));
        }
    }
}
