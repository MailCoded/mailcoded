using System.Text.Json;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Mcp;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class SenderAuthenticationMcpTests
{
    [Fact]
    public async Task An_agent_can_search_for_verified_mail_and_the_result_is_omitted_when_never_evaluated()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create("mcp-verified");
        await using var host = McpHost.Create(workspace.DatabasePath);

        var account = await StoreSeeder.AddAccountAsync(host.Store, ct);
        var folder = await StoreSeeder.AddInboxAsync(host.Store, account, ct);

        await Add(host, folder, 1, "Verified", DmarcResult.Pass, ct);
        await Add(host, folder, 2, "Failing", DmarcResult.Fail, ct);
        await Add(host, folder, 3, "Unknown", DmarcResult.Unknown, ct);
        await Add(host, folder, 4, "Never evaluated", null, ct);

        var tools = new MailTools(host);

        Assert.Equal(["Verified"], Subjects(await tools.SearchAsync(Args("""{"query":"is:verified"}"""), ct)));

        var rest = Subjects(await tools.SearchAsync(Args("""{"query":"-is:verified"}"""), ct));
        Assert.Equal(["Failing", "Never evaluated", "Unknown"], rest.Order(StringComparer.Ordinal));

        using var all = JsonDocument.Parse(await tools.SearchAsync(Args("""{"query":"body"}"""), ct));
        var hits = all.RootElement.GetProperty("hits").EnumerateArray().ToDictionary(h => h.GetProperty("subject").GetString()!);
        Assert.Equal("pass", hits["Verified"].GetProperty("dmarc").GetString());
        Assert.Equal("fail", hits["Failing"].GetProperty("dmarc").GetString());
        Assert.Equal("unknown", hits["Unknown"].GetProperty("dmarc").GetString());
        Assert.False(hits["Never evaluated"].TryGetProperty("dmarc", out _));

        using var read = JsonDocument.Parse(await tools.ReadAsync(Args("""{"id":1,"fetch_if_missing":false}"""), ct));
        Assert.Equal("pass", read.RootElement.GetProperty("dmarc").GetString());

        using var thread = JsonDocument.Parse(await tools.ThreadAsync(Args("""{"id":1}"""), ct));
        Assert.Equal("pass", thread.RootElement.GetProperty("messages")[0].GetProperty("dmarc").GetString());
    }

    [Fact]
    public void No_tool_can_change_which_authserv_ids_are_trusted()
    {
        var names = typeof(MailTools).GetMethods().Select(m => m.Name).Concat(typeof(McpHost).GetMethods().Select(m => m.Name));

        Assert.DoesNotContain(names, n => n.Contains("Trust", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Authserv", StringComparison.OrdinalIgnoreCase));
    }

    private static Task Add(McpHost host, Mailcoded.Core.Domain.Primitives.FolderId folder, uint uid, string subject, DmarcResult? dmarc, CancellationToken ct) =>
        StoreSeeder.AddMessageAsync(
            host.Store, folder, uid, $"mcp-verified-{uid}@example.test", subject, "Ada <ada@example.test>",
            "golden@example.test", new DateTimeOffset(2024, 1, (int)uid, 0, 0, 0, TimeSpan.Zero), "body " + subject, 100, ct,
            dmarc: dmarc);

    private static List<string> Subjects(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("subject").GetString() ?? string.Empty)];
    }

    private static ToolArgs Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
        return new ToolArgs(values);
    }
}
