using System.Text.Json;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Mcp;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class MessageSignalsMcpTests
{
    [Fact]
    public async Task An_agent_can_triage_on_the_signals_and_unknown_is_omitted_not_false()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create("mcp-signals");
        await using var host = McpHost.Create(workspace.DatabasePath);

        var account = await StoreSeeder.AddAccountAsync(host.Store, ct);
        var folder = await StoreSeeder.AddInboxAsync(host.Store, account, ct);

        await Add(host, folder, 1, "Newsletter", automated: true, encrypted: false, ct);
        await Add(host, folder, 2, "Personal note", automated: false, encrypted: false, ct);
        await Add(host, folder, 3, "Sealed", automated: false, encrypted: true, ct);
        await Add(host, folder, 4, "Old mail", automated: null, encrypted: null, ct);

        var tools = new MailTools(host);

        Assert.Equal(["Newsletter"], Subjects(await tools.SearchAsync(Args("""{"query":"is:automated"}"""), ct)));
        Assert.Equal(["Sealed"], Subjects(await tools.SearchAsync(Args("""{"query":"is:encrypted"}"""), ct)));

        var human = Subjects(await tools.SearchAsync(Args("""{"query":"-is:automated -is:encrypted"}"""), ct));
        Assert.Equal(["Personal note"], human);

        using var all = JsonDocument.Parse(await tools.SearchAsync(Args("""{"query":"body"}"""), ct));
        var unknown = all.RootElement.GetProperty("hits").EnumerateArray().Single(h => h.GetProperty("subject").GetString() == "Old mail");
        Assert.False(unknown.TryGetProperty("automated", out _));
        Assert.False(unknown.TryGetProperty("encrypted", out _));
    }

    [Fact]
    public async Task Read_and_thread_carry_the_signals()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create("mcp-signals-read");
        await using var host = McpHost.Create(workspace.DatabasePath);

        var account = await StoreSeeder.AddAccountAsync(host.Store, ct);
        var folder = await StoreSeeder.AddInboxAsync(host.Store, account, ct);
        await Add(host, folder, 1, "Newsletter", automated: true, encrypted: false, ct);

        var tools = new MailTools(host);

        using var read = JsonDocument.Parse(await tools.ReadAsync(Args("""{"id":1,"fetch_if_missing":false}"""), ct));
        Assert.True(read.RootElement.GetProperty("automated").GetBoolean());
        Assert.False(read.RootElement.GetProperty("encrypted").GetBoolean());

        using var thread = JsonDocument.Parse(await tools.ThreadAsync(Args("""{"id":1}"""), ct));
        var message = thread.RootElement.GetProperty("messages")[0];
        Assert.True(message.GetProperty("automated").GetBoolean());
    }

    private static async Task Add(
        McpHost host, Mailcoded.Core.Domain.Primitives.FolderId folder, uint uid, string subject,
        bool? automated, bool? encrypted, CancellationToken ct) =>
        await StoreSeeder.AddMessageAsync(
            host.Store, folder, uid, $"mcp-signals-{uid}@example.test", subject, "Ada <ada@example.test>",
            "golden@example.test", new DateTimeOffset(2024, 1, (int)uid, 0, 0, 0, TimeSpan.Zero), "body " + subject, 100, ct,
            automated: automated, encrypted: encrypted);

    private static List<string> Subjects(string json)
    {
        using var document = JsonDocument.Parse(json);
        var subjects = new List<string>();
        foreach (var hit in document.RootElement.GetProperty("hits").EnumerateArray())
            subjects.Add(hit.GetProperty("subject").GetString() ?? string.Empty);
        return subjects;
    }

    private static ToolArgs Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
        return new ToolArgs(values);
    }
}
