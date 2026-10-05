using System.Text.Json;
using Mailcoded.Core.Application;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Mcp;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class AttachmentTextMcpTests
{
    [Fact]
    public async Task An_agent_search_reports_which_attachment_matched_and_only_for_the_operator()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create("mcp-attachment-text");
        await using var host = McpHost.Create(workspace.DatabasePath);

        var account = await StoreSeeder.AddAccountAsync(host.Store, ct);
        var inbox = await StoreSeeder.AddInboxAsync(host.Store, account, ct);
        var day = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var id = await StoreSeeder.AddMessageAsync(
            host.Store, inbox, 1, "a@example.test", "Order confirmation", "Ada <ada@example.test>", "golden@example.test", day, "Your order shipped.", 100, ct);
        await host.Store.SetAttachmentTextsAsync(
            id,
            [new AttachmentTextEntry { Index = 0, MimeType = "text/csv", FileName = "../../etc/passwd.csv", Text = "sku,qty\ncopper wiring,200" }],
            AttachmentTextIndexing.ExtractorVersion,
            ct);

        var tools = new MailTools(host);

        using var found = JsonDocument.Parse(await tools.SearchAsync(Args("""{"query":"attachment:wiring"}"""), ct));
        var hit = Assert.Single(found.RootElement.GetProperty("hits").EnumerateArray().ToList());
        var match = hit.GetProperty("attachment_match");
        Assert.Equal(0, match.GetProperty("index").GetInt32());
        Assert.Equal("text/csv", match.GetProperty("mime").GetString());
        Assert.DoesNotContain("/", match.GetProperty("filename").GetString(), StringComparison.Ordinal);
        Assert.Contains("wiring", match.GetProperty("snippet").GetString(), StringComparison.Ordinal);

        using var bare = JsonDocument.Parse(await tools.SearchAsync(Args("""{"query":"wiring"}"""), ct));
        Assert.Equal(0, bare.RootElement.GetProperty("hits").GetArrayLength());

        using var other = JsonDocument.Parse(await tools.SearchAsync(Args("""{"query":"order"}"""), ct));
        Assert.False(Assert.Single(other.RootElement.GetProperty("hits").EnumerateArray().ToList()).TryGetProperty("attachment_match", out _));
    }

    [Fact]
    public void The_search_tool_description_tells_an_agent_what_attachment_search_covers_and_what_it_does_not()
    {
        Assert.Contains("attachment:", ToolText.SearchDescription, StringComparison.Ordinal);
        Assert.Contains("never PDFs", ToolText.SearchDescription, StringComparison.Ordinal);
    }

    private static ToolArgs Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
        return new ToolArgs(values);
    }
}
