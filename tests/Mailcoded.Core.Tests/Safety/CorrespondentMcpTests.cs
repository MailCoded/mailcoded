using System.Reflection;
using Mailcoded.Core.Application;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Mcp;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class CorrespondentMcpTests
{
    [Fact]
    public async Task The_mcp_catalog_offers_no_list_of_the_users_correspondents()
    {
        using var workspace = TempWorkspace.Create("mcp-correspondents");
        await using var host = McpHost.Create(workspace.DatabasePath);

        var tools = ToolCatalog.Build(new MailTools(host)).ToArray();

        Assert.DoesNotContain(tools, static t => Mentions(t.ProtocolTool.Name));
        Assert.DoesNotContain(tools, static t => Mentions(t.ProtocolTool.Description ?? string.Empty));
    }

    [Fact]
    public void The_mcp_adapter_cannot_reach_the_suggestion_service_at_all()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var service = typeof(CorrespondentService);

        foreach (var type in typeof(McpHost).Assembly.GetTypes())
        {
            foreach (var field in type.GetFields(all)) Assert.NotEqual(service, field.FieldType);
            foreach (var property in type.GetProperties(all)) Assert.NotEqual(service, property.PropertyType);
            foreach (var method in type.GetMethods(all))
            {
                Assert.NotEqual(service, method.ReturnType);
                foreach (var parameter in method.GetParameters()) Assert.NotEqual(service, parameter.ParameterType);
            }
        }
    }

    private static bool Mentions(string text) => text.Contains("correspondent", StringComparison.OrdinalIgnoreCase);
}
