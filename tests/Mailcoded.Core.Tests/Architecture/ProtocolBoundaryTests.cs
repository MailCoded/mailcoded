using System.Reflection;
using Xunit;

namespace Mailcoded.Core.Tests.Architecture;

public sealed class ProtocolBoundaryTests
{
    [Fact]
    public void The_protocol_assembly_references_nothing_but_the_bcl()
    {
        var offenders = ArchitectureAssemblies.Protocol
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("Mailcoded", StringComparison.Ordinal)
                || name.StartsWith("MailKit", StringComparison.Ordinal)
                || name.StartsWith("MimeKit", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal)
                || name.StartsWith("SQLitePCL", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Identity", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Mailcoded.Protocol must depend on the BCL alone: " + string.Join(", ", offenders) + ". "
            + "docs/rpc.md promises a third party can implement a client from this contract, and a "
            + "client author will not take an IMAP stack, an OAuth stack and a native SQLite binary "
            + "to obtain a DTO. If the wire needs a fact from the engine, project it in the daemon's "
            + "WireMapper instead.");
    }

    [Fact]
    public void The_engine_cannot_name_the_wire()
    {
        var names = ArchitectureAssemblies.Core
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);

        Assert.DoesNotContain("Mailcoded.Protocol", names, StringComparer.Ordinal);
    }

    [Fact]
    public void Every_rpc_method_name_is_documented_in_the_protocol_assembly()
    {
        var declared = typeof(Mailcoded.Protocol.RpcMethods)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.All(declared, m => Assert.False(string.IsNullOrWhiteSpace(m)));
    }
}
