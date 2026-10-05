using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Cli.Commands;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class ServerDraftCliTests : IDisposable
{
    private readonly SurfaceWorkspace _workspace = new("cli-server-drafts");

    public void Dispose() => _workspace.Dispose();

    private SqliteStore OpenStore() =>
        new(new SqliteStoreOptions { DatabasePath = _workspace.DatabasePath }, new ManualClock());

    private async Task SeedAsync(CancellationToken ct)
    {
        using var store = OpenStore();
        await StoreSeeder.AddAccountAsync(store, ct);
    }

    private Task<CliRun> RunAsync(string verb, string[] arguments, CancellationToken ct, bool json = true) =>
        CliRunner.RunAsync(_workspace.DatabasePath, verb, arguments, ct, json);

    private static EnvironmentScope Unlocked() => EnvironmentScope.Set((AccountServerDraftsCommand.OverrideEnvVar, "1"));

    private static EnvironmentScope Locked() => EnvironmentScope.Set((AccountServerDraftsCommand.OverrideEnvVar, null));

    private bool Enabled()
    {
        using var store = OpenStore();
        return Require.Ref(store.GetAccount(new AccountId(1), TestContext.Current.CancellationToken), "the account").SaveDraftsToServer;
    }

    private string WriteBody(string text)
    {
        var path = _workspace.PathTo("body-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task The_setting_is_off_until_someone_turns_it_on_and_showing_it_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await RunAsync("account server-drafts", [], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.False(json.RootElement.GetProperty("save_drafts_to_server").GetBoolean());
        Assert.False(json.RootElement.GetProperty("changed").GetBoolean());
        Assert.False(Enabled());
    }

    [Fact]
    public async Task Turning_it_on_needs_both_the_unlock_and_yes_because_it_is_a_non_interactive_run()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        using (Locked())
        {
            var bare = await RunAsync("account server-drafts", ["on"], ct);
            Assert.Equal(ExitCodes.Validation, bare.ExitCode);
            Assert.Contains(AccountServerDraftsCommand.OverrideEnvVar, bare.StdErr + bare.StdOut, StringComparison.Ordinal);

            var flagOnly = await RunAsync("account server-drafts", ["on", "--yes"], ct);
            Assert.Equal(ExitCodes.Validation, flagOnly.ExitCode);
        }

        using (Unlocked())
        {
            var envOnly = await RunAsync("account server-drafts", ["on"], ct);
            Assert.Equal(ExitCodes.Validation, envOnly.ExitCode);
        }

        Assert.False(Enabled());
    }

    [Fact]
    public async Task With_the_unlock_and_yes_it_turns_on_is_audited_as_a_warning_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        using var unlocked = Unlocked();
        var first = await RunAsync("account server-drafts", ["on", "--yes"], ct);
        var second = await RunAsync("account server-drafts", ["on", "--yes"], ct);

        Assert.Equal(ExitCodes.Ok, first.ExitCode);
        using (var json = JsonDocument.Parse(first.StdOut))
        {
            Assert.True(json.RootElement.GetProperty("save_drafts_to_server").GetBoolean());
            Assert.True(json.RootElement.GetProperty("changed").GetBoolean());
        }

        using (var json = JsonDocument.Parse(second.StdOut))
            Assert.False(json.RootElement.GetProperty("changed").GetBoolean());

        Assert.True(Enabled());
        using var store = OpenStore();
        var row = Assert.Single(StoreQuery.Strings(store, "SELECT level || '|' || detail FROM sync_log WHERE event = 'server_draft_setting'", ct));
        Assert.Equal("warn|enabled=true", row);
    }

    [Fact]
    public async Task Turning_it_off_needs_no_unlock_because_it_only_narrows_what_happens()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        using (Unlocked()) await RunAsync("account server-drafts", ["on", "--yes"], ct);
        Assert.True(Enabled());

        using var locked = Locked();
        var off = await RunAsync("account server-drafts", ["off"], ct);

        Assert.Equal(ExitCodes.Ok, off.ExitCode);
        Assert.False(Enabled());
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("ON")]
    [InlineData("true")]
    [InlineData("drop table accounts")]
    public async Task An_action_that_is_not_on_off_or_show_is_a_usage_error(string action)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await RunAsync("account server-drafts", [action], ct);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
        Assert.False(Enabled());
    }

    [Fact]
    public async Task Account_list_shows_the_setting()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        using (Unlocked()) await RunAsync("account server-drafts", ["on", "--yes"], ct);
        var run = await RunAsync("account list", [], ct);

        using var json = JsonDocument.Parse(run.StdOut);
        var account = Assert.Single(json.RootElement.GetProperty("accounts").EnumerateArray());
        Assert.True(account.GetProperty("save_drafts_to_server").GetBoolean());

        var text = await RunAsync("account list", [], ct, json: false);
        Assert.Contains("Drafts Folder", text.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_draft_on_an_account_that_has_not_opted_in_reports_off_and_never_touches_the_server()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await RunAsync("draft", ["--to", "bob@example.org", "--subject", "Hi", "--body-file", WriteBody("Hello.")], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.Equal("off", json.RootElement.GetProperty("server_draft").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("server_draft_reason").ValueKind);
    }

    [Fact]
    public async Task An_opted_in_account_with_no_drafts_folder_still_drafts_locally_and_says_why()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        using (Unlocked()) await RunAsync("account server-drafts", ["on", "--yes"], ct);

        var run = await RunAsync("draft", ["--to", "bob@example.org", "--subject", "Hi", "--body-file", WriteBody("Hello.")], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.Equal("failed", json.RootElement.GetProperty("server_draft").GetString());
        Assert.Equal("no-drafts-folder", json.RootElement.GetProperty("server_draft_reason").GetString());

        var draftId = json.RootElement.GetProperty("draft_id").GetInt64();
        using var store = OpenStore();
        Assert.NotNull(store.GetOutbox(draftId, ct));

        var text = await RunAsync("draft", ["--to", "bob@example.org", "--subject", "Hi", "--body-file", WriteBody("Hello.")], ct, json: false);
        Assert.Contains("not saved", text.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_outbox_listing_names_a_server_draft_only_when_there_is_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await RunAsync("draft", ["--to", "bob@example.org", "--subject", "Hi", "--body-file", WriteBody("Hello.")], ct);

        var run = await RunAsync("outbox", [], ct);

        using var json = JsonDocument.Parse(run.StdOut);
        var row = Assert.Single(json.RootElement.GetProperty("outbox").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("server_draft").ValueKind);
    }

    [Fact]
    public void The_help_names_the_unlock_and_promises_that_nothing_is_deleted()
    {
        var help = HelpText.For("account server-drafts");

        Assert.Contains(AccountServerDraftsCommand.OverrideEnvVar, help, StringComparison.Ordinal);
        Assert.Contains("MOVE", help, StringComparison.Ordinal);
        Assert.Contains("Nothing is ever deleted", help, StringComparison.Ordinal);
        Assert.Contains("account server-drafts", HelpText.Overview, StringComparison.Ordinal);
        Assert.Contains(AccountServerDraftsCommand.OverrideEnvVar, HelpText.Overview, StringComparison.Ordinal);
        Assert.Contains("server_draft", HelpText.For("send-draft"), StringComparison.Ordinal);
    }
}
