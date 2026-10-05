using System.Text.Json;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Mcp;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class ServerDraftMcpTests
{
    private static ToolArgs Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
        return new ToolArgs(values);
    }

    private const string PreviewArgs = "{\"account_id\":1,\"to\":[\"bob@example.org\"],\"subject\":\"Hi\",\"body\":\"Hello.\"}";

    [Fact]
    public async Task Send_preview_reports_off_for_an_account_that_has_not_opted_in()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create("mcp-server-draft-off");
        await using var host = McpHost.Create(workspace.DatabasePath);
        await StoreSeeder.AddAccountAsync(host.Store, ct);
        var tools = new MailTools(host);

        using var preview = JsonDocument.Parse(await tools.SendPreviewAsync(Args(PreviewArgs), ct));

        Assert.Equal("off", preview.RootElement.GetProperty("server_draft").GetString());
    }

    [Fact]
    public async Task An_agent_that_cannot_send_still_drafts_and_a_server_failure_is_reported_not_raised()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create("mcp-server-draft-failed");
        await using var host = McpHost.Create(workspace.DatabasePath);
        var account = await StoreSeeder.AddAccountAsync(host.Store, ct);
        await host.Store.SetSaveDraftsToServerAsync(account, true, ct);
        var tools = new MailTools(host);

        using var preview = JsonDocument.Parse(await tools.SendPreviewAsync(Args(PreviewArgs), ct));

        Assert.Equal("failed", preview.RootElement.GetProperty("server_draft").GetString());
        Assert.Equal("no-drafts-folder", preview.RootElement.GetProperty("server_draft_reason").GetString());
        Assert.False(preview.RootElement.GetProperty("send_gate").GetProperty("allowed").GetBoolean());
        Assert.Single(host.Send.ListOutbox(null, ct));
    }

    [Fact]
    public async Task The_mcp_surface_has_no_tool_that_changes_the_setting()
    {
        using var workspace = TempWorkspace.Create("mcp-server-draft-tools");
        await using var host = McpHost.Create(workspace.DatabasePath);

        foreach (var tool in ToolCatalog.Build(new MailTools(host)).ToArray())
        {
            Assert.DoesNotContain("server_draft", tool.ProtocolTool.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("save_drafts", tool.ProtocolTool.Name, StringComparison.OrdinalIgnoreCase);
        }

        await Task.CompletedTask;
    }
}
