using System.Text.Json;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Mcp;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class AutoCategoryMcpTests
{
    [Fact]
    public async Task An_agent_finds_and_reads_category_tags_through_the_plain_tag_operator()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create("mcp-auto-category");
        await using var host = McpHost.Create(workspace.DatabasePath);

        var account = await StoreSeeder.AddAccountAsync(host.Store, ct);
        var folder = await StoreSeeder.AddInboxAsync(host.Store, account, ct);
        await host.Store.IngestEnvelopesAsync(
            folder,
            [
                StoreSeed.Envelope(1, subject: "Friend request", from: "Friends <notification@facebookmail.com>"),
                StoreSeed.Envelope(2, subject: "Lunch", from: "Ada <ada@example.test>"),
            ],
            ReferencesThreader.Instance,
            ct);

        var tools = new MailTools(host);

        using var found = JsonDocument.Parse(await tools.SearchAsync(Args("""{"query":"tag:auto.social"}"""), ct));
        var hit = Assert.Single(found.RootElement.GetProperty("hits").EnumerateArray());
        Assert.Equal("Friend request", hit.GetProperty("subject").GetString());

        using var read = JsonDocument.Parse(await tools.ReadAsync(Args("""{"id":1,"fetch_if_missing":false}"""), ct));
        Assert.Contains(
            "auto.social",
            read.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

        using var plain = JsonDocument.Parse(await tools.ReadAsync(Args("""{"id":2,"fetch_if_missing":false}"""), ct));
        Assert.DoesNotContain(
            "auto.social",
            plain.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    private static ToolArgs Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
        return new ToolArgs(values);
    }
}
