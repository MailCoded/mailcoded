using System.Reflection;
using Mailcoded.Core.Tests.Daemon;
using Mailcoded.Core.Tests.Docs;
using Mailcoded.Protocol;
using Mailcoded.Protocol.TypeScript;
using Xunit;

namespace Mailcoded.Core.Tests.Protocol;

/// <summary>The checked-in TypeScript is derived from the C# records. A DTO change that does not
/// regenerate it ships a client that disagrees with the daemon it talks to.</summary>
public sealed class TypeScriptTypesTests
{
    private static readonly Assembly Protocol = typeof(ProtocolJsonContext).Assembly;

    private static readonly string GeneratedPath = Path.Combine(
        DocumentationTests.LocateRepositoryRoot(), "packages", "protocol", "src", "generated.ts");

    [Fact]
    public void The_checked_in_types_match_the_protocol_assembly()
    {
        var generated = Emit();

        if (GoldenFiles.UpdateRequested())
        {
            File.WriteAllText(GeneratedPath, generated);
            return;
        }

        Assert.True(File.Exists(GeneratedPath), $"{GeneratedPath} is missing. Run 'npm run gen:types'.");

        var current = XmlDocs.NormalizeNewlines(File.ReadAllText(GeneratedPath));

        Assert.True(
            current == generated,
            "packages/protocol/src/generated.ts is stale. " + TypeScriptEmitter.FirstDifference(current, generated)
            + " Run 'npm run gen:types' and commit the result in the same change as the DTO.");
    }

    [Fact]
    public void Emission_is_deterministic() => Assert.Equal(Emit(), Emit());

    [Fact]
    public void Every_method_has_its_params_and_result_records()
    {
        foreach (var method in Constants(typeof(RpcMethods)))
        {
            Assert.True(
                Protocol.GetType($"Mailcoded.Protocol.{method.Name}Params") is not null,
                $"'{method.GetRawConstantValue()}' has no {method.Name}Params record.");
            Assert.True(
                Protocol.GetType($"Mailcoded.Protocol.{method.Name}Result") is not null,
                $"'{method.GetRawConstantValue()}' has no {method.Name}Result record.");
        }

        foreach (var notification in Constants(typeof(RpcNotifications)))
        {
            Assert.True(
                Protocol.GetType($"Mailcoded.Protocol.{notification.Name}Notification") is not null,
                $"'{notification.GetRawConstantValue()}' has no {notification.Name}Notification record.");
        }
    }

    /// <summary>Guards against a filter in the emitter silently dropping part of the contract.</summary>
    [Fact]
    public void Every_method_notification_and_error_code_appears_in_the_output()
    {
        var generated = Emit();

        foreach (var method in Constants(typeof(RpcMethods)))
            Assert.Contains($"\"{method.GetRawConstantValue()}\": {{ params:", generated, StringComparison.Ordinal);

        foreach (var notification in Constants(typeof(RpcNotifications)))
            Assert.Contains($"\"{notification.GetRawConstantValue()}\": ", generated, StringComparison.Ordinal);

        foreach (var code in Enum.GetValues<RpcErrorCode>())
            Assert.Contains($"  {code}: {(int)code},", generated, StringComparison.Ordinal);

        Assert.Contains($"export const PROTOCOL_VERSION = {ProtocolConstants.Version};", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_members_are_not_optional_and_nullable_ones_are()
    {
        var generated = Emit();

        Assert.Contains("  id: number;", generated, StringComparison.Ordinal);
        Assert.Contains("  threadKey?: string;", generated, StringComparison.Ordinal);
        Assert.Contains("  bodyHtml?: string;", generated, StringComparison.Ordinal);
        Assert.Contains("  flags: readonly string[];", generated, StringComparison.Ordinal);
        Assert.Contains("  fetchIfMissing?: boolean;", generated, StringComparison.Ordinal);
    }

    private static string Emit() => TypeScriptEmitter.Emit(Protocol, XmlDocs.Load(Protocol));

    private static IEnumerable<FieldInfo> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral);
}
