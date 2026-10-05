using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class SearchFolderScopeCliTests : IDisposable
{
    private readonly SurfaceWorkspace _workspace = new("cli-folder-scope");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Search_leaves_Trash_and_Junk_out_until_the_query_or_a_flag_names_a_Folder()
    {
        var ct = TestContext.Current.CancellationToken;
        var junk = await SeedAsync(ct);

        Assert.Equal(["Invoice inbox"], Subjects(await RunAsync(["invoice", "--order", "date"], ct)));

        Assert.Equal(
            ["Invoice spam", "Invoice trashed", "Invoice inbox"],
            Subjects(await RunAsync(["invoice in:anywhere", "--order", "date"], ct)));

        Assert.Equal(["Invoice trashed"], Subjects(await RunAsync(["invoice in:trash"], ct)));
        Assert.Equal(["Invoice spam"], Subjects(await RunAsync(["invoice", "--folder", junk.ToString(System.Globalization.CultureInfo.InvariantCulture)], ct)));
    }

    [Fact]
    public async Task A_negated_anywhere_is_reported_and_the_default_still_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await RunAsync(["invoice -in:anywhere"], ct);

        Assert.Equal(["Invoice inbox"], Subjects(run));
        using var document = JsonDocument.Parse(run.StdOut);
        Assert.NotEqual(0, document.RootElement.GetProperty("errors").GetArrayLength());
    }

    private async Task<long> SeedAsync(CancellationToken ct)
    {
        using var store = new Mailcoded.Core.Store.SqliteStore(
            new Mailcoded.Core.Store.SqliteStoreOptions { DatabasePath = _workspace.DatabasePath },
            new ManualClock());

        var account = await StoreSeeder.AddAccountAsync(store, ct);
        var inbox = await StoreSeeder.AddInboxAsync(store, account, ct);
        var others = await store.UpsertFoldersAsync(
            account,
            [
                new RemoteFolder { Path = FolderPath.Create("Trash"), Role = FolderRole.Trash },
                new RemoteFolder { Path = FolderPath.Create("Spam"), Role = FolderRole.Junk },
            ],
            ct);

        var day = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await StoreSeeder.AddMessageAsync(store, inbox, 1, "a@example.test", "Invoice inbox", "Ada <ada@example.test>", "golden@example.test", day, "invoice body", 100, ct);
        await StoreSeeder.AddMessageAsync(store, others[0], 1, "b@example.test", "Invoice trashed", "Ada <ada@example.test>", "golden@example.test", day.AddDays(1), "invoice body", 100, ct);
        await StoreSeeder.AddMessageAsync(store, others[1], 1, "c@example.test", "Invoice spam", "Ada <ada@example.test>", "golden@example.test", day.AddDays(2), "invoice body", 100, ct);

        return others[1].Value;
    }

    private Task<CliRun> RunAsync(string[] arguments, CancellationToken ct) =>
        CliRunner.RunAsync(_workspace.DatabasePath, "search", arguments, ct);

    private static List<string> Subjects(CliRun run)
    {
        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        using var document = JsonDocument.Parse(run.StdOut);
        var subjects = new List<string>();
        foreach (var hit in document.RootElement.GetProperty("hits").EnumerateArray())
            subjects.Add(hit.GetProperty("subject").GetString() ?? string.Empty);

        return subjects;
    }
}
