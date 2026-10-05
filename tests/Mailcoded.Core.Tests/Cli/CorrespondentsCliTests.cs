using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Core.Domain.Correspondents;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class CorrespondentsCliTests : IDisposable
{
    private readonly SurfaceWorkspace _workspace = new("cli-correspondents");

    public void Dispose() => _workspace.Dispose();

    private SqliteStore OpenStore() =>
        new(new SqliteStoreOptions { DatabasePath = _workspace.DatabasePath }, new ManualClock(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    private static RemoteEnvelope Sent(uint uid, string to, DateTimeOffset date, string? cc = null) =>
        StoreSeed.Envelope(uid, from: StoreSeeder.AccountEmail, to: to, cc: cc, date: date);

    private async Task SeedAsync(CancellationToken ct)
    {
        using var store = OpenStore();
        var account = await StoreSeeder.AddAccountAsync(store, ct);
        var ids = await store.UpsertFoldersAsync(
            account,
            [new RemoteFolder { Path = FolderPath.Create("Sent"), Role = FolderRole.Sent }],
            ct);

        var when = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        await store.IngestEnvelopesAsync(
            ids[0],
            [
                Sent(1, "\"Ada Example\" <ada@example.org>", when),
                Sent(2, "ada@example.org, bob@example.net", when.AddDays(1)),
                Sent(3, "Ada@Example.org", when.AddDays(2), cc: "Cy <cy@example.org>"),
            ],
            null,
            ct);
    }

    [Fact]
    public async Task The_json_lists_people_best_first_with_names_uses_and_dates()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", [], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var document = JsonDocument.Parse(run.StdOut);
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        Assert.Equal(StoreSeeder.AccountEmail, root.GetProperty("email").GetString());

        var rows = root.GetProperty("correspondents").EnumerateArray().ToList();
        Assert.Equal(["ada@example.org", "bob@example.net", "cy@example.org"], rows.Select(r => r.GetProperty("address").GetString()).Order(StringComparer.Ordinal).Take(3));
        Assert.Equal("ada@example.org", rows[0].GetProperty("address").GetString());
        Assert.Equal(3, rows[0].GetProperty("uses").GetInt32());
        Assert.Equal("Ada Example", rows[0].GetProperty("display_name").GetString());
        Assert.Equal("2024-03-03T00:00:00.000Z", rows[0].GetProperty("last_used_utc").GetString());

        var bob = rows.Single(r => r.GetProperty("address").GetString() == "bob@example.net");
        Assert.Equal(JsonValueKind.Null, bob.GetProperty("display_name").ValueKind);
    }

    [Fact]
    public async Task A_prefix_and_a_limit_narrow_the_list()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var byName = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", ["exa"], ct);
        using (var document = JsonDocument.Parse(byName.StdOut))
        {
            Assert.Equal(
                ["ada@example.org"],
                document.RootElement.GetProperty("correspondents").EnumerateArray().Select(r => r.GetProperty("address").GetString()));
        }

        var limited = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", ["--limit", "2"], ct);
        using (var document = JsonDocument.Parse(limited.StdOut))
        {
            Assert.Equal(2, document.RootElement.GetProperty("count").GetInt32());
        }

        var none = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", ["zzz"], ct);
        Assert.Equal(ExitCodes.Ok, none.ExitCode);
        using (var document = JsonDocument.Parse(none.StdOut))
        {
            Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        }
    }

    [Fact]
    public async Task The_json_says_whether_the_limit_cut_the_list_and_never_offers_a_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var cut = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", ["--limit", "2"], ct);
        using (var document = JsonDocument.Parse(cut.StdOut))
        {
            Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("next_cursor").ValueKind);
        }

        foreach (var arguments in new[] { new[] { "--limit", "3" }, [] })
        {
            var whole = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", arguments, ct);
            using var document = JsonDocument.Parse(whole.StdOut);
            Assert.False(document.RootElement.GetProperty("truncated").GetBoolean());
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("many")]
    public async Task A_limit_outside_one_to_a_hundred_is_a_usage_error(string limit)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", ["--limit", limit], ct);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
    }

    [Fact]
    public async Task More_than_one_prefix_is_refused_rather_than_guessed_at()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", ["ada", "bob"], ct);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
    }

    [Fact]
    public async Task The_human_output_is_one_line_per_person()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", [], ct, json: false);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        var lines = run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("ada@example.org", lines[0], StringComparison.Ordinal);
        Assert.Contains("3x", lines[0], StringComparison.Ordinal);
        Assert.Contains("2024-03-03", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("Ada Example", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hostile_display_name_cannot_reach_the_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var store = OpenStore())
        {
            var account = await StoreSeeder.AddAccountAsync(store, ct);
            await store.RecordCorrespondentsAsync(
                account,
                [new NamedAddress(EmailAddress.Parse("evil@example.org"), "\u001b[2J‮fake\u0007 name")],
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ct);
        }

        var human = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", [], ct, json: false);

        Assert.DoesNotContain('\u001b', human.StdOut);
        Assert.DoesNotContain('‮', human.StdOut);
        Assert.DoesNotContain('\u0007', human.StdOut);
        Assert.Contains("fake name", human.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_call_is_audited_with_a_count_and_never_the_prefix_or_an_address()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", ["ada"], ct);

        using var store = OpenStore();
        var rows = StoreQuery.Strings(store, "SELECT level || '|' || COALESCE(detail, '') FROM sync_log WHERE event = 'cli_correspondents'", ct);
        var row = Assert.Single(rows);
        Assert.Equal("info|returned=1", row);
    }

    [Fact]
    public async Task With_two_accounts_the_account_option_picks_the_list()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        using (var store = OpenStore())
        {
            var other = await StoreSeeder.AddAccountAsync(store, ct, "second@example.test");
            await store.RecordCorrespondentsAsync(
                other,
                [new NamedAddress(EmailAddress.Parse("zed@example.org"), null)],
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ct);
        }

        var ambiguous = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", [], ct);
        Assert.NotEqual(ExitCodes.Ok, ambiguous.ExitCode);

        var picked = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", ["--account", "second@example.test"], ct);
        using var document = JsonDocument.Parse(picked.StdOut);
        Assert.Equal(
            ["zed@example.org"],
            document.RootElement.GetProperty("correspondents").EnumerateArray().Select(r => r.GetProperty("address").GetString()));
    }

    [Fact]
    public async Task An_unknown_account_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await CliRunner.RunAsync(_workspace.DatabasePath, "correspondents", ["--account", "nobody@example.test"], ct);

        Assert.Equal(ExitCodes.NotFound, run.ExitCode);
    }

    [Fact]
    public void The_verb_is_listed_in_the_overview_and_has_its_own_help()
    {
        Assert.Contains("correspondents", HelpText.Overview, StringComparison.Ordinal);
        var help = HelpText.For("correspondents");
        Assert.Contains("Sent mail only", HelpText.Overview, StringComparison.Ordinal);
        Assert.Contains("--limit", help, StringComparison.Ordinal);
        Assert.Contains("Received mail adds no one", help, StringComparison.Ordinal);
        Assert.Contains("\"truncated\": true", help, StringComparison.Ordinal);
        Assert.DoesNotContain("contact", help, StringComparison.OrdinalIgnoreCase);
    }
}
