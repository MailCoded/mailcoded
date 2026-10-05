using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class FoldersTreeCliTests : IDisposable
{
    private readonly SurfaceWorkspace _workspace = new("cli-folder-tree");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task The_human_output_is_an_indented_tree_with_implied_parents_as_bare_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await CliRunner.RunAsync(_workspace.DatabasePath, "folders", [], ct, json: false);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        var lines = run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

        Assert.Equal(["INBOX", "Work", "Clients", "Acme", "Plans"], lines.Select(NameColumn));

        Assert.StartsWith("     ", lines[3]);
        Assert.Contains("      Acme", lines[3], StringComparison.Ordinal);
        Assert.Contains("    Clients", lines[2], StringComparison.Ordinal);

        var clients = lines[2];
        Assert.DoesNotContain("unread", clients, StringComparison.Ordinal);
        Assert.Equal(string.Empty, clients[..38].Trim());
        Assert.Contains("unread", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_json_stays_a_flat_array_in_tree_order_with_the_hierarchy_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await CliRunner.RunAsync(_workspace.DatabasePath, "folders", [], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        using var document = JsonDocument.Parse(run.StdOut);
        var folders = document.RootElement.GetProperty("folders").EnumerateArray().ToList();

        Assert.Equal(4, document.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(["INBOX", "Work", "Work/Clients/Acme", "Work/Plans"], folders.Select(f => f.GetProperty("name").GetString()));
        Assert.Equal(["INBOX", "Work", "Acme", "Plans"], folders.Select(f => f.GetProperty("leaf").GetString()));
        Assert.Equal([0, 0, 2, 1], folders.Select(f => f.GetProperty("depth").GetInt32()));
        Assert.All(folders, f => Assert.Equal(".", f.GetProperty("delimiter").GetString()));

        var work = folders[1].GetProperty("id").GetInt64();
        Assert.Equal(JsonValueKind.Null, folders[0].GetProperty("parent_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, folders[1].GetProperty("parent_id").ValueKind);
        Assert.Equal(work, folders[2].GetProperty("parent_id").GetInt64());
        Assert.Equal(work, folders[3].GetProperty("parent_id").GetInt64());
    }

    [Fact]
    public async Task A_hostile_folder_name_is_defanged_in_the_tree_and_intact_in_the_json()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var store = new Mailcoded.Core.Store.SqliteStore(
                   new Mailcoded.Core.Store.SqliteStoreOptions { DatabasePath = _workspace.DatabasePath },
                   new ManualClock()))
        {
            var account = await StoreSeeder.AddAccountAsync(store, ct);
            await store.UpsertFoldersAsync(
                account,
                [new RemoteFolder { Path = FolderPath.Create("Work/\u001b[2J\u202Eevil\u0007") }],
                ct);
        }

        var human = await CliRunner.RunAsync(_workspace.DatabasePath, "folders", [], ct, json: false);

        Assert.DoesNotContain('\u001b', human.StdOut);
        Assert.DoesNotContain('\u202E', human.StdOut);
        Assert.DoesNotContain('\u0007', human.StdOut);

        var json = await CliRunner.RunAsync(_workspace.DatabasePath, "folders", [], ct);
        using var document = JsonDocument.Parse(json.StdOut);
        var leaf = document.RootElement.GetProperty("folders")[0].GetProperty("leaf").GetString();

        Assert.Equal("\u001b[2J\u202Eevil\u0007", leaf);
    }

    [Fact]
    public async Task A_deep_folder_does_not_push_its_name_off_the_line()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var store = new Mailcoded.Core.Store.SqliteStore(
                   new Mailcoded.Core.Store.SqliteStoreOptions { DatabasePath = _workspace.DatabasePath },
                   new ManualClock()))
        {
            var account = await StoreSeeder.AddAccountAsync(store, ct);
            await store.UpsertFoldersAsync(
                account,
                [new RemoteFolder { Path = FolderPath.Create(string.Join('/', Enumerable.Range(0, 30).Select(i => "level" + i))) }],
                ct);
        }

        var run = await CliRunner.RunAsync(_workspace.DatabasePath, "folders", [], ct, json: false);

        foreach (var line in run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(line.TrimEnd('\r').Length <= 38 + 60, line);
    }

    [Fact]
    public async Task Importing_into_a_listed_folder_keeps_the_servers_delimiter()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var directory = _workspace.PathTo("eml");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "050-unsubscribe-mailto.eml"), Fixtures.Read("050-unsubscribe-mailto.eml"));

        var run = await CliRunner.RunAsync(_workspace.DatabasePath, "import-eml", [directory, "--folder", "Work/Plans"], ct);
        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        var listed = await CliRunner.RunAsync(_workspace.DatabasePath, "folders", [], ct);
        using var document = JsonDocument.Parse(listed.StdOut);
        var folders = document.RootElement.GetProperty("folders").EnumerateArray().ToList();

        Assert.Equal(4, folders.Count);
        Assert.All(folders, f => Assert.Equal(".", f.GetProperty("delimiter").GetString()));
        var plans = folders.Single(f => f.GetProperty("name").GetString() == "Work/Plans");
        Assert.Equal(1, plans.GetProperty("total").GetInt32());
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        using var store = new Mailcoded.Core.Store.SqliteStore(
            new Mailcoded.Core.Store.SqliteStoreOptions { DatabasePath = _workspace.DatabasePath },
            new ManualClock());

        var account = await StoreSeeder.AddAccountAsync(store, ct);
        await store.UpsertFoldersAsync(
            account,
            [
                new RemoteFolder { Path = FolderPath.Create("INBOX", '.'), Role = FolderRole.Inbox },
                new RemoteFolder { Path = FolderPath.Create("Work.Clients.Acme", '.') },
                new RemoteFolder { Path = FolderPath.Create("Work.Plans", '.') },
                new RemoteFolder { Path = FolderPath.Create("Work", '.') },
            ],
            ct);
    }

    private static string NameColumn(string line) => line[(line.LastIndexOf("  ", StringComparison.Ordinal) + 2)..].Trim();
}
