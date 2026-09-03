using System.Reflection;
using Mailcoded.Cli.Commands;
using Mailcoded.Core.Application;
using Mailcoded.Protocol;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Mcp;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class NoDeleteSurfaceTests
{
    private static readonly string[] Forbidden = ["delete", "expunge", "trash", "purge", "remove", "destroy"];

    private static readonly string[] KnownCliVerbs =
    [
        "search", "read", "thread", "tag", "draft", "send-preview", "send-draft", "query",
        "stats", "health", "folders", "sync", "account add", "import-eml",
    ];

    [Fact]
    public async Task McpCatalog_AdvertisesNoMailRemovalTool()
    {
        using var workspace = new SurfaceWorkspace("mcp-catalog");
        await using var host = McpHost.Create(workspace.DatabasePath);

        var tools = ToolCatalog.Build(new MailTools(host)).ToArray();
        Assert.NotEmpty(tools);

        foreach (var tool in tools)
        {
            var name = tool.ProtocolTool.Name;
            Assert.False(string.IsNullOrWhiteSpace(name));

            foreach (var word in Forbidden)
            {
                Assert.False(
                    name.Contains(word, StringComparison.OrdinalIgnoreCase),
                    $"the MCP surface advertises a '{word}' tool named '{name}'");
            }
        }

        Assert.Contains(tools, static t => t.ProtocolTool.Name == ToolText.SearchTool);
        Assert.Contains(tools, static t => t.ProtocolTool.Name == ToolText.SendDraftTool);
    }

    [Fact]
    public async Task McpToolDescriptions_RepeatTheUntrustedContentWarning()
    {
        using var workspace = new SurfaceWorkspace("mcp-descriptions");
        await using var host = McpHost.Create(workspace.DatabasePath);

        foreach (var tool in ToolCatalog.Build(new MailTools(host)).ToArray())
        {
            Assert.Contains(
                ToolText.UntrustedContent,
                tool.ProtocolTool.Description ?? string.Empty,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("expunge")]
    [InlineData("trash")]
    [InlineData("purge")]
    [InlineData("rm")]
    [InlineData("message.delete")]
    public void CliVerbTable_HasNoRemovalVerb(string verb) => Assert.Null(CommandTable.Find(verb));

    [Fact]
    public void CliVerbTable_ListsOnlyTheDocumentedVerbs()
    {
        var names = CommandTable.Names().Split(", ", StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in Forbidden)
        {
            Assert.DoesNotContain(names, name => name.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var verb in KnownCliVerbs)
        {
            Assert.NotNull(CommandTable.Find(verb));
            Assert.Contains(verb, names);
        }

        Assert.Contains("version", names);
        Assert.Contains("help", names);
    }

    [Fact]
    public void RpcMethodTable_HasNoRemovalMethod()
    {
        foreach (var field in typeof(RpcMethods).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!field.IsLiteral || field.FieldType != typeof(string)) continue;

            var method = (string)field.GetRawConstantValue()!;
            foreach (var word in Forbidden)
            {
                Assert.False(
                    method.Contains(word, StringComparison.OrdinalIgnoreCase),
                    $"RpcMethods exposes '{method}'");
            }
        }
    }

    [Fact]
    public void PostureMatrix_HasNoDeleteCapability()
    {
        foreach (var capability in Enum.GetNames<AgentCapability>())
        {
            foreach (var word in Forbidden)
            {
                Assert.False(
                    capability.Contains(word, StringComparison.OrdinalIgnoreCase),
                    $"AgentCapability declares '{capability}'");
            }
        }
    }
}
