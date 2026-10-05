using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Cli.Commands;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class SenderAuthenticationCliTests : IDisposable
{
    private readonly SurfaceWorkspace _workspace = new("cli-sender-auth");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Showing_the_trust_list_changes_nothing_and_says_what_to_do_when_it_is_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        await AddAccountAsync("a@example.com", [], "imap.example.com", ct);

        var run = await RunAsync("account trust", [], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.Empty(json.RootElement.GetProperty("effective").EnumerateArray());
        Assert.False(json.RootElement.GetProperty("changed").GetBoolean());

        var text = await CliRunner.RunAsync(_workspace.DatabasePath, "account trust", [], ct, json: false);
        Assert.Contains("No authserv-id is trusted", text.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gmail_account_shows_the_built_in_id_separately_from_what_was_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        await AddAccountAsync("a@gmail.com", ["mx.example.net"], "imap.gmail.com", ct);

        using var json = JsonDocument.Parse((await RunAsync("account trust", [], ct)).StdOut);

        Assert.Equal(["mx.example.net"], Strings(json.RootElement.GetProperty("configured")));
        Assert.Equal(["mx.google.com"], Strings(json.RootElement.GetProperty("from_provider")));
        Assert.Equal(["mx.google.com", "mx.example.net"], Strings(json.RootElement.GetProperty("effective")));
    }

    [Fact]
    public async Task Changing_the_list_needs_confirmation_and_an_unattended_run_needs_the_environment_unlock_too()
    {
        var ct = TestContext.Current.CancellationToken;
        await AddAccountAsync("a@example.com", [], "imap.example.com", ct);

        var bare = await RunAsync("account trust", ["--authserv-id", "mx.example.net"], ct);
        Assert.NotEqual(ExitCodes.Ok, bare.ExitCode);
        Assert.Contains(AccountTrustCommand.OverrideEnvVar, bare.StdErr + bare.StdOut, StringComparison.Ordinal);

        var flagOnly = await RunAsync("account trust", ["--authserv-id", "mx.example.net", "--yes"], ct);
        Assert.NotEqual(ExitCodes.Ok, flagOnly.ExitCode);

        Assert.Empty(StoredIds(1, ct));
    }

    [Fact]
    public async Task With_the_unlock_the_list_is_replaced_normalized_and_the_results_cleared()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await AddAccountAsync("a@example.com", [], "imap.example.com", ct);

        using (var store = OpenStore())
        {
            var folder = await StoreSeeder.AddInboxAsync(store, account, ct);
            await StoreSeeder.AddMessageAsync(
                store, folder, 1, "m@example.test", "Verified", "Ada <ada@example.com>", "a@example.com",
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), "body", 10, ct,
                dmarc: Mailcoded.Core.Domain.Signals.DmarcResult.Pass);
        }

        var set = await WithUnlockAsync(() => RunAsync("account trust", ["--authserv-id", "MX.Example.NET,*.example.org", "--yes"], ct));

        Assert.Equal(ExitCodes.Ok, set.ExitCode);
        using (var json = JsonDocument.Parse(set.StdOut))
        {
            Assert.True(json.RootElement.GetProperty("changed").GetBoolean());
            Assert.Equal(["mx.example.net", "*.example.org"], Strings(json.RootElement.GetProperty("effective")));
        }

        Assert.Equal(["mx.example.net", "*.example.org"], StoredIds(1, ct));
        Assert.Empty(HitSubjects(await RunAsync("search", ["is:verified"], ct)));

        var again = await WithUnlockAsync(() => RunAsync("account trust", ["--authserv-id", "mx.example.net,*.example.org", "--yes"], ct));
        using var repeat = JsonDocument.Parse(again.StdOut);
        Assert.False(repeat.RootElement.GetProperty("changed").GetBoolean());

        var cleared = await WithUnlockAsync(() => RunAsync("account trust", ["--none", "--yes"], ct));
        Assert.Equal(ExitCodes.Ok, cleared.ExitCode);
        Assert.Empty(StoredIds(1, ct));
    }

    [Theory]
    [InlineData("--authserv-id", "not a host")]
    [InlineData("--authserv-id", "*.com")]
    [InlineData("--authserv-id", "mx.example.net; dmarc=pass")]
    [InlineData("--authserv-id", "")]
    public async Task An_invalid_id_is_refused_before_anything_changes(string option, string value)
    {
        var ct = TestContext.Current.CancellationToken;
        await AddAccountAsync("a@example.com", ["mx.example.net"], "imap.example.com", ct);

        var run = await WithUnlockAsync(() => RunAsync("account trust", [option, value, "--yes"], ct));

        Assert.NotEqual(ExitCodes.Ok, run.ExitCode);
        Assert.Equal(["mx.example.net"], StoredIds(1, ct));
    }

    [Fact]
    public async Task Both_an_id_and_none_is_a_usage_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await AddAccountAsync("a@example.com", [], "imap.example.com", ct);

        var run = await WithUnlockAsync(() => RunAsync("account trust", ["--authserv-id", "mx.example.net", "--none", "--yes"], ct));

        Assert.NotEqual(ExitCodes.Ok, run.ExitCode);
    }

    [Fact]
    public async Task Account_add_takes_the_ids_and_account_list_reports_the_effective_trust()
    {
        var ct = TestContext.Current.CancellationToken;

        var add = await RunAsync(
            "account add",
            [
                "--email", "new@example.com", "--imap-host", "imap.example.com", "--no-password",
                "--trust-authserv-id", "MX.example.net", "--trust-authserv-id", "mx2.example.net,mx2.example.net",
            ],
            ct);

        Assert.Equal(ExitCodes.Ok, add.ExitCode);
        Assert.Equal(["mx.example.net", "mx2.example.net"], StoredIds(1, ct));

        using var list = JsonDocument.Parse((await RunAsync("account list", [], ct)).StdOut);
        var entry = list.RootElement.GetProperty("accounts")[0];
        Assert.Equal(["mx.example.net", "mx2.example.net"], Strings(entry.GetProperty("trusted_authserv_ids")));
    }

    [Fact]
    public async Task Account_add_with_a_bad_id_stores_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var add = await RunAsync(
            "account add",
            ["--email", "new@example.com", "--imap-host", "imap.example.com", "--no-password", "--trust-authserv-id", "bad id"],
            ct);

        Assert.NotEqual(ExitCodes.Ok, add.ExitCode);

        using var list = JsonDocument.Parse((await RunAsync("account list", [], ct)).StdOut);
        Assert.Equal(0, list.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Account_add_for_an_existing_address_cannot_change_the_trust_list()
    {
        var ct = TestContext.Current.CancellationToken;
        await AddAccountAsync("a@example.com", ["mx.example.net"], "imap.example.com", ct);

        var add = await RunAsync(
            "account add",
            [
                "--email", "a@example.com", "--imap-host", "imap.example.com", "--no-password",
                "--trust-authserv-id", "mx.attacker.example.org",
            ],
            ct);

        Assert.Equal(ExitCodes.Ok, add.ExitCode);
        Assert.Equal(["mx.example.net"], StoredIds(1, ct));
    }

    [Fact]
    public async Task Imported_mail_is_never_verified_even_when_the_account_trusts_the_header()
    {
        var ct = TestContext.Current.CancellationToken;
        await AddAccountAsync("a@example.com", ["mx.example.net"], "imap.example.com", ct);

        var directory = _workspace.PathTo("eml");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "046.eml"), Fixtures.Read("046-auth-results-pass.eml"));

        Assert.Equal(ExitCodes.Ok, (await RunAsync("import-eml", [directory, "--account", "1"], ct)).ExitCode);

        Assert.Empty(HitSubjects(await RunAsync("search", ["is:verified"], ct)));
        Assert.Single(HitSubjects(await RunAsync("search", ["-is:verified"], ct)));

        using var read = JsonDocument.Parse((await RunAsync("read", ["1", "--no-fetch"], ct)).StdOut);
        Assert.Equal("unknown", read.RootElement.GetProperty("dmarc").GetString());
    }

    [Fact]
    public async Task Imported_mail_stays_unverified_after_the_account_starts_trusting_the_header()
    {
        var ct = TestContext.Current.CancellationToken;
        await AddAccountAsync("a@example.com", [], "imap.example.com", ct);

        var directory = _workspace.PathTo("eml");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "046.eml"), Fixtures.Read("046-auth-results-pass.eml"));
        Assert.Equal(ExitCodes.Ok, (await RunAsync("import-eml", [directory, "--account", "1"], ct)).ExitCode);

        var trust = await WithUnlockAsync(() => RunAsync("account trust", ["--authserv-id", "mx.example.net", "--yes"], ct));
        Assert.Equal(ExitCodes.Ok, trust.ExitCode);

        Assert.Empty(HitSubjects(await RunAsync("search", ["is:verified"], ct)));
        using var read = JsonDocument.Parse((await RunAsync("read", ["1", "--no-fetch"], ct)).StdOut);
        Assert.Equal("unknown", read.RootElement.GetProperty("dmarc").GetString());
    }

    [Fact]
    public async Task Read_search_and_thread_report_the_result_and_null_when_it_was_never_evaluated()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await AddAccountAsync("a@example.com", [], "imap.example.com", ct);

        using (var store = OpenStore())
        {
            var folder = await StoreSeeder.AddInboxAsync(store, account, ct);
            await StoreSeeder.AddMessageAsync(
                store, folder, 1, "p@example.test", "Passing", "Ada <ada@example.com>", "a@example.com",
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), "body", 10, ct,
                dmarc: Mailcoded.Core.Domain.Signals.DmarcResult.Pass);
            await StoreSeeder.AddMessageAsync(
                store, folder, 2, "n@example.test", "Never evaluated", "Ada <ada@example.com>", "a@example.com",
                new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero), "body", 10, ct);
        }

        using var passing = JsonDocument.Parse((await RunAsync("read", ["1", "--no-fetch"], ct)).StdOut);
        Assert.Equal("pass", passing.RootElement.GetProperty("dmarc").GetString());

        using var never = JsonDocument.Parse((await RunAsync("read", ["2", "--no-fetch"], ct)).StdOut);
        Assert.Equal(JsonValueKind.Null, never.RootElement.GetProperty("dmarc").ValueKind);

        using var search = JsonDocument.Parse((await RunAsync("search", ["body", "--order", "date"], ct)).StdOut);
        var hits = search.RootElement.GetProperty("hits").EnumerateArray().ToList();
        Assert.Equal("pass", hits.Single(h => h.GetProperty("subject").GetString() == "Passing").GetProperty("dmarc").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            hits.Single(h => h.GetProperty("subject").GetString() == "Never evaluated").GetProperty("dmarc").ValueKind);

        using var thread = JsonDocument.Parse((await RunAsync("thread", ["1"], ct)).StdOut);
        Assert.Equal("pass", thread.RootElement.GetProperty("messages")[0].GetProperty("dmarc").GetString());
    }

    [Fact]
    public void The_trust_verb_is_routed_documented_and_listed_with_the_other_account_verbs()
    {
        Assert.Contains("trust", CommandTable.AccountSubcommands().Split(", "));
        Assert.NotNull(CommandTable.Find("account trust"));

        var help = HelpText.For("account trust");
        Assert.Contains("--authserv-id", help, StringComparison.Ordinal);
        Assert.Contains(AccountTrustCommand.OverrideEnvVar, help, StringComparison.Ordinal);
        Assert.Contains("account trust", HelpText.Overview, StringComparison.Ordinal);
    }

    private Task<CliRun> RunAsync(string verb, string[] arguments, CancellationToken ct) =>
        CliRunner.RunAsync(_workspace.DatabasePath, verb, arguments, ct);

    private SqliteStore OpenStore() =>
        new(new SqliteStoreOptions { DatabasePath = _workspace.DatabasePath }, new ManualClock());

    private async Task<AccountId> AddAccountAsync(string email, string[] trusted, string imapHost, CancellationToken ct)
    {
        using var store = OpenStore();

        return await store.AddAccountAsync(
            new AccountConfig
            {
                Email = email,
                Provider = ProviderKind.Imap,
                Imap = new ImapConfig { Host = imapHost },
                Auth = AuthKind.Password,
                SecretRef = "imap:" + email,
                TrustedAuthservIds = trusted,
            },
            ct);
    }

    private IReadOnlyList<string> StoredIds(long accountId, CancellationToken ct)
    {
        using var store = OpenStore();
        return Require.Ref(store.GetAccount(new AccountId(accountId), ct), "the account").TrustedAuthservIds;
    }

    private static async Task<CliRun> WithUnlockAsync(Func<Task<CliRun>> run)
    {
        Environment.SetEnvironmentVariable(AccountTrustCommand.OverrideEnvVar, "1");

        try
        {
            return await run();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AccountTrustCommand.OverrideEnvVar, null);
        }
    }

    private static List<string> Strings(JsonElement array) =>
        [.. array.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];

    private static List<string> HitSubjects(CliRun run)
    {
        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var document = JsonDocument.Parse(run.StdOut);
        return [.. document.RootElement.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("subject").GetString() ?? string.Empty)];
    }
}
