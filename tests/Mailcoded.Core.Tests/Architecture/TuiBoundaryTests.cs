using NetArchTest.Rules;
using Xunit;

namespace Mailcoded.Core.Tests.Architecture;

/// <summary>The TUI is the proof that docs/rpc.md is enough: it may only reach the wire.</summary>
public sealed class TuiBoundaryTests
{
    private static readonly string[] Forbidden =
    [
        "Mailcoded.Core",
        "MailKit",
        "MimeKit",
        "Microsoft.Data.Sqlite",
        "SQLitePCL",
        "Microsoft.Identity.Client",
    ];

    [Fact]
    public void The_tui_host_is_reachable_from_these_tests()
    {
        Assert.True(
            ArchitectureAssemblies.Tui is not null,
            $"'{ArchitectureAssemblies.TuiAssemblyName}' could not be loaded, so every rule below would pass over "
            + "an empty set. Mailcoded.Core.Tests must keep its ProjectReference to Mailcoded.Tui.");
    }

    [Fact]
    public void The_tui_reaches_the_engine_only_through_the_wire()
    {
        var tui = ArchitectureAssemblies.Tui;
        Assert.NotNull(tui);

        var result = Types.InAssembly(tui)
            .That().ResideInNamespace("Mailcoded")
            .ShouldNot().HaveDependencyOnAny(Forbidden)
            .GetResult();

        var names = (result.FailingTypes ?? []).Select(t => t.FullName ?? t.Name).ToArray();

        Assert.True(
            result.IsSuccessful,
            "RULE: mailcoded-tui references Mailcoded.Protocol and nothing else. It exists to prove a third party "
            + "can write a client from docs/rpc.md without taking the engine, MailKit or SQLite. A reference to "
            + "Mailcoded.Core makes that claim false. Offending types: " + string.Join(", ", names));
    }

    [Fact]
    public void The_tui_assembly_references_only_protocol_and_the_framework()
    {
        var tui = ArchitectureAssemblies.Tui;
        Assert.NotNull(tui);

        foreach (var reference in tui.GetReferencedAssemblies())
        {
            var name = reference.Name ?? string.Empty;
            if (name.StartsWith("System", StringComparison.Ordinal) || name is "netstandard" or "mscorlib") continue;

            Assert.True(
                name is "Mailcoded.Protocol",
                $"mailcoded-tui references '{name}'. Only Mailcoded.Protocol and the BCL are permitted.");
        }
    }
}
