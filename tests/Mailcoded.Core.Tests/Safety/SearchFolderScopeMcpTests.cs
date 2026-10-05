using System.Text.Json;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Mcp;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class SearchFolderScopeMcpTests
{
    [Fact]
    public async Task An_agent_search_leaves_Trash_and_Junk_out_unless_it_names_a_Folder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create("mcp-folder-scope");
        await using var host = McpHost.Create(workspace.DatabasePath);

        var account = await StoreSeeder.AddAccountAsync(host.Store, ct);
        var inbox = await StoreSeeder.AddInboxAsync(host.Store, account, ct);
        var others = await host.Store.UpsertFoldersAsync(
            account,
            [
                new RemoteFolder { Path = FolderPath.Create("Trash"), Role = FolderRole.Trash },
                new RemoteFolder { Path = FolderPath.Create("Spam"), Role = FolderRole.Junk },
            ],
            ct);

        var day = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await StoreSeeder.AddMessageAsync(host.Store, inbox, 1, "a@example.test", "Invoice inbox", "Ada <ada@example.test>", "golden@example.test", day, "invoice body", 100, ct);
        await StoreSeeder.AddMessageAsync(host.Store, others[0], 1, "b@example.test", "Invoice trashed", "Ada <ada@example.test>", "golden@example.test", day.AddDays(1), "invoice body", 100, ct);
        await StoreSeeder.AddMessageAsync(host.Store, others[1], 1, "c@example.test", "Invoice spam", "Ada <ada@example.test>", "golden@example.test", day.AddDays(2), "invoice body", 100, ct);

        var tools = new MailTools(host);

        Assert.Equal(["Invoice inbox"], Subjects(await tools.SearchAsync(Args("""{"query":"invoice","order":"date"}"""), ct)));
        Assert.Equal(
            ["Invoice spam", "Invoice trashed", "Invoice inbox"],
            Subjects(await tools.SearchAsync(Args("""{"query":"invoice in:anywhere","order":"date"}"""), ct)));

        var junk = others[1].Value;
        Assert.Equal(
            ["Invoice spam"],
            Subjects(await tools.SearchAsync(Args($$"""{"query":"invoice","folder_id":{{junk}}}"""), ct)));
    }

    private static ToolArgs Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
        return new ToolArgs(values);
    }

    private static List<string> Subjects(string json)
    {
        using var document = JsonDocument.Parse(json);
        var subjects = new List<string>();
        foreach (var hit in document.RootElement.GetProperty("hits").EnumerateArray())
            subjects.Add(hit.GetProperty("subject").GetString() ?? string.Empty);
        return subjects;
    }
}
