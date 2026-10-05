using System.Text.Json;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Mcp;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class UnsubscribeMcpTests
{
    [Fact]
    public async Task Read_lists_the_options_and_warns_that_the_links_must_not_be_opened()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create("mcp-unsubscribe");
        await using var host = McpHost.Create(workspace.DatabasePath);

        var account = await StoreSeeder.AddAccountAsync(host.Store, ct);
        var folder = await StoreSeeder.AddInboxAsync(host.Store, account, ct);
        var headers = new SignalHeaders
        {
            ListUnsubscribe = "<mailto:leave@example.org?subject=Leave>, <https://lists.example.org/u/abc>",
            ListUnsubscribePost = "List-Unsubscribe=One-Click",
        };

        await StoreSeeder.AddMessageAsync(
            host.Store, folder, 1, "mcp-unsub-1@example.test", "Newsletter", "Ada <ada@example.test>", "golden@example.test",
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), "body", 100, ct, headers: headers);
        await StoreSeeder.AddMessageAsync(
            host.Store, folder, 2, "mcp-unsub-2@example.test", "Plain", "Ada <ada@example.test>", "golden@example.test",
            new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero), "body", 100, ct, headers: SignalHeaders.None);

        var tools = new MailTools(host);

        using var read = JsonDocument.Parse(await tools.ReadAsync(Args("""{"id":1,"fetch_if_missing":false}"""), ct));
        var unsubscribe = read.RootElement.GetProperty("unsubscribe");
        Assert.Equal("leave@example.org", unsubscribe.GetProperty("mailto").GetProperty("address").GetString());
        Assert.Equal("Leave", unsubscribe.GetProperty("mailto").GetProperty("subject").GetString());
        Assert.False(unsubscribe.GetProperty("mailto").TryGetProperty("body", out _));
        Assert.Equal(1, unsubscribe.GetProperty("web").GetArrayLength());
        Assert.Equal("https://lists.example.org/u/abc", unsubscribe.GetProperty("web")[0].GetString());
        Assert.True(unsubscribe.GetProperty("one_click").GetBoolean());
        Assert.Contains("do not open", unsubscribe.GetProperty("note").GetString(), StringComparison.Ordinal);

        using var plain = JsonDocument.Parse(await tools.ReadAsync(Args("""{"id":2,"fetch_if_missing":false}"""), ct));
        Assert.False(plain.RootElement.TryGetProperty("unsubscribe", out _));
    }

    [Fact]
    public async Task No_tool_opens_a_link_or_unsubscribes_on_its_own()
    {
        using var workspace = TempWorkspace.Create("mcp-unsubscribe-catalog");
        await using var host = McpHost.Create(workspace.DatabasePath);

        var names = ToolCatalog.Build(new MailTools(host)).Select(t => t.ProtocolTool.Name).ToArray();

        Assert.DoesNotContain(names, n => n.Contains("unsubscribe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("open", StringComparison.OrdinalIgnoreCase) || n.Contains("fetch_url", StringComparison.OrdinalIgnoreCase));
    }

    private static ToolArgs Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
        return new ToolArgs(values);
    }
}
