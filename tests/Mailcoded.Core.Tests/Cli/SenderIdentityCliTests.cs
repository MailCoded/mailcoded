using System.Text;
using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Cli.Commands;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class SenderIdentityCliTests : IDisposable
{
    private const string Own = "golden@example.test";

    private readonly SurfaceWorkspace _workspace = new("cli-sender-identity");

    public void Dispose() => _workspace.Dispose();

    private SqliteStore OpenStore() =>
        new(new SqliteStoreOptions { DatabasePath = _workspace.DatabasePath }, new ManualClock());

    private async Task<AccountId> SeedAsync(CancellationToken ct)
    {
        using var store = OpenStore();
        return await StoreSeeder.AddAccountAsync(store, ct);
    }

    private async Task SeedIdentityAsync(string address, string? name, string? replyTo, CancellationToken ct)
    {
        using var store = OpenStore();
        Assert.True(SenderIdentity.TryCreate(address, name, replyTo, out var identity, out var error), error);
        await store.EditIdentitiesAsync(new AccountId(1), list => SenderIdentities.With(list, identity), ct);
    }

    private async Task SeedSignaturesAsync(CancellationToken ct)
    {
        using var store = OpenStore();
        await store.EditSignaturesAsync(
            new AccountId(1),
            book => book.Set("work", "Alex Example\nExample Pty").Set("short", "Alex")
                .WithDefault(SignatureUse.New, "work").WithDefault(SignatureUse.Reply, "short"),
            ct);
    }

    private Task<CliRun> RunAsync(string verb, string[] arguments, CancellationToken ct, bool json = true) =>
        CliRunner.RunAsync(_workspace.DatabasePath, verb, arguments, ct, json);

    private static EnvironmentScope Unlocked() => EnvironmentScope.Set((AccountIdentityCommand.OverrideEnvVar, "1"));

    private static EnvironmentScope Locked() => EnvironmentScope.Set((AccountIdentityCommand.OverrideEnvVar, null));

    private static string RawOf(OutboxRecord draft) =>
        Encoding.UTF8.GetString(draft.Raw).Replace("\r\n", "\n", StringComparison.Ordinal);

    private OutboxRecord Draft(JsonElement root)
    {
        using var store = OpenStore();
        return Require.Ref(store.GetOutbox(root.GetProperty("draft_id").GetInt64(), TestContext.Current.CancellationToken), "the draft");
    }

    private async Task<string> ImportOriginalAsync(string to, string cc, CancellationToken ct)
    {
        var directory = _workspace.PathTo("eml");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "orig.eml"),
            "From: Ada <ada@example.net>\r\n"
            + $"To: {to}\r\n"
            + $"Cc: {cc}\r\n"
            + "Subject: Question\r\n"
            + "Message-ID: <orig-1@example.net>\r\n"
            + "Date: Mon, 01 Jan 2024 00:00:00 +0000\r\n"
            + "\r\n"
            + "Hello there.\r\n");

        var run = await RunAsync("import-eml", [directory], ct);
        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        return directory;
    }

    [Fact]
    public async Task Listing_shows_the_account_address_and_every_identity_and_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await SeedIdentityAsync("sales@example.test", "Sales", "help@example.test", ct);

        var run = await RunAsync("account identity", ["list"], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        var identity = Assert.Single(json.RootElement.GetProperty("identities").EnumerateArray());
        Assert.Equal("sales@example.test", identity.GetProperty("address").GetString());
        Assert.Equal("Sales", identity.GetProperty("display_name").GetString());
        Assert.Equal("help@example.test", identity.GetProperty("reply_to").GetString());
        Assert.False(json.RootElement.GetProperty("changed").GetBoolean());

        var text = await RunAsync("account identity", ["list"], ct, json: false);
        Assert.Contains("always     " + Own, text.StdOut, StringComparison.Ordinal);
        Assert.Contains("sales@example.test", text.StdOut, StringComparison.Ordinal);
        Assert.Contains("reply-to help@example.test", text.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_an_identity_needs_the_environment_unlock_as_well_as_yes()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        using (Locked())
        {
            var bare = await RunAsync("account identity", ["add", "sales@example.test"], ct);
            Assert.Equal(ExitCodes.Validation, bare.ExitCode);
            Assert.Contains(AccountIdentityCommand.OverrideEnvVar, bare.StdErr + bare.StdOut, StringComparison.Ordinal);

            var flagOnly = await RunAsync("account identity", ["add", "sales@example.test", "--yes"], ct);
            Assert.Equal(ExitCodes.Validation, flagOnly.ExitCode);
        }

        using (Unlocked())
        {
            var envOnly = await RunAsync("account identity", ["add", "sales@example.test"], ct);
            Assert.Equal(ExitCodes.Validation, envOnly.ExitCode);
        }

        using var store = OpenStore();
        Assert.Empty(Require.Ref(store.GetAccount(new AccountId(1), ct), "the account").Identities);
    }

    [Fact]
    public async Task With_the_unlock_and_yes_an_identity_is_added_audited_and_listed()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        using var unlocked = Unlocked();
        var run = await RunAsync(
            "account identity",
            ["add", "Sales@Example.test", "--yes", "--name", "Sales Desk", "--reply-to", "help@example.test"],
            ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.True(json.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal("Sales@example.test", json.RootElement.GetProperty("identities")[0].GetProperty("address").GetString());

        using var store = OpenStore();
        var rows = StoreQuery.Strings(store, "SELECT level || '|' || COALESCE(detail, '') FROM sync_log WHERE event = 'identity_added'", ct);
        var row = Assert.Single(rows);
        Assert.StartsWith("warn|", row, StringComparison.Ordinal);
        Assert.Contains("address=Sales@example.test", row, StringComparison.Ordinal);

        var again = await RunAsync(
            "account identity",
            ["add", "Sales@example.test", "--yes", "--name", "Sales Desk", "--reply-to", "help@example.test"],
            ct);
        using var repeat = JsonDocument.Parse(again.StdOut);
        Assert.False(repeat.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal(1, StoreQuery.Scalar(store, "SELECT COUNT(*) FROM sync_log WHERE event = 'identity_added'", ct));
    }

    [Theory]
    [InlineData("not-an-address", null, null)]
    [InlineData("Sales <sales@example.test>", null, null)]
    [InlineData("sales@example.test", "Sales\r\nBcc: victim@example.org", null)]
    [InlineData("sales@example.test", null, "nope")]
    public async Task A_bad_identity_is_a_validation_error_and_stores_nothing(string address, string? name, string? replyTo)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var arguments = new List<string> { "add", address, "--yes" };
        if (name is not null) arguments.AddRange(["--name", name]);
        if (replyTo is not null) arguments.AddRange(["--reply-to", replyTo]);

        using var unlocked = Unlocked();
        var run = await RunAsync("account identity", [.. arguments], ct);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
        using var store = OpenStore();
        Assert.Empty(Require.Ref(store.GetAccount(new AccountId(1), ct), "the account").Identities);
    }

    [Fact]
    public async Task Forgetting_an_identity_needs_no_unlock_because_it_only_narrows_who_you_can_send_as()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await SeedIdentityAsync("sales@example.test", null, null, ct);

        using var locked = Locked();
        var run = await RunAsync("account identity", ["forget", "SALES@example.test"], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.True(json.RootElement.GetProperty("changed").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("identities").EnumerateArray());

        using var store = OpenStore();
        Assert.Equal(1, StoreQuery.Scalar(store, "SELECT COUNT(*) FROM sync_log WHERE event = 'identity_forgotten'", ct));

        var again = await RunAsync("account identity", ["forget", "sales@example.test"], ct);
        Assert.Equal(ExitCodes.Ok, again.ExitCode);
        using var repeat = JsonDocument.Parse(again.StdOut);
        Assert.False(repeat.RootElement.GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task The_account_address_itself_cannot_be_forgotten_and_stays_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await RunAsync("account identity", ["forget", Own], ct);
        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.False(json.RootElement.GetProperty("changed").GetBoolean());

        var draft = await RunAsync("draft", ["--to", "bob@example.org", "--subject", "Hi", "--body-file", WriteBody("Hello."), "--from", Own], ct);
        Assert.Equal(ExitCodes.Ok, draft.ExitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("frobnicate")]
    [InlineData("add")]
    [InlineData("forget")]
    [InlineData("list extra")]
    public async Task A_missing_or_unknown_action_is_a_validation_error(string line)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var arguments = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ExitCodes.Validation, (await RunAsync("account identity", arguments, ct)).ExitCode);
    }

    [Fact]
    public async Task Draft_from_an_address_the_account_does_not_own_exits_two_with_a_stable_name_and_queues_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await RunAsync("draft", ["--to", "bob@example.org", "--subject", "Hi", "--body-file", WriteBody("Hello."), "--from", "ceo@example.org"], ct);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
        using var error = JsonDocument.Parse(run.StdErr);
        var detail = error.RootElement.GetProperty("error");
        Assert.Equal("sender_not_allowed", detail.GetProperty("name").GetString());
        Assert.Equal(-32602, detail.GetProperty("code").GetInt32());
        Assert.Contains("account identity list", detail.GetProperty("hint").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ceo@example.org", detail.GetProperty("message").GetString(), StringComparison.Ordinal);

        using var store = OpenStore();
        Assert.Empty(store.ListOutbox(null, ct));
    }

    [Fact]
    public async Task Draft_from_an_identity_works_in_any_case_and_carries_the_name_and_reply_to()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await SeedIdentityAsync("sales@example.test", "Sales Desk", "help@example.test", ct);

        var run = await RunAsync("draft", ["--to", "bob@example.org", "--subject", "Hi", "--body-file", WriteBody("Hello."), "--from", "SALES@EXAMPLE.TEST"], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.Equal("sales@example.test", json.RootElement.GetProperty("from").GetString());

        var raw = RawOf(Draft(json.RootElement));
        Assert.Contains("From: Sales Desk <sales@example.test>", raw, StringComparison.Ordinal);
        Assert.Contains("Reply-To: help@example.test", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draft_uses_the_new_message_default_and_reports_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await SeedSignaturesAsync(ct);

        var run = await RunAsync("draft", ["--to", "bob@example.org", "--subject", "Hi", "--body-file", WriteBody("Hello.\n")], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.Equal("work", json.RootElement.GetProperty("signature").GetString());
        Assert.Contains("\nHello.\n\n-- \nAlex Example\nExample Pty\n", RawOf(Draft(json.RootElement)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draft_no_signature_and_a_named_signature_override_the_default()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await SeedSignaturesAsync(ct);
        var body = WriteBody("Hello.");

        var none = await RunAsync("draft", ["--to", "bob@example.org", "--subject", "Hi", "--body-file", body, "--no-signature"], ct);
        using var noneJson = JsonDocument.Parse(none.StdOut);
        Assert.Equal(JsonValueKind.Null, noneJson.RootElement.GetProperty("signature").ValueKind);
        Assert.DoesNotContain("-- \n", RawOf(Draft(noneJson.RootElement)), StringComparison.Ordinal);

        var named = await RunAsync("draft", ["--to", "bob@example.org", "--subject", "Hi", "--body-file", body, "--signature", "SHORT"], ct);
        using var namedJson = JsonDocument.Parse(named.StdOut);
        Assert.Contains("\n-- \nAlex\n", RawOf(Draft(namedJson.RootElement)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--signature", "missing")]
    [InlineData("--signature", "bad name")]
    public async Task Draft_with_an_unknown_signature_is_a_validation_error_and_queues_nothing(string option, string value)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await RunAsync("draft", ["--to", "bob@example.org", "--subject", "Hi", "--body-file", WriteBody("Hello."), option, value], ct);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
        using var store = OpenStore();
        Assert.Empty(store.ListOutbox(null, ct));
    }

    [Fact]
    public async Task Asking_for_a_signature_and_for_none_is_a_validation_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await RunAsync(
            "draft",
            ["--to", "bob@example.org", "--subject", "Hi", "--body-file", WriteBody("Hello."), "--signature", "work", "--no-signature"],
            ct);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
    }

    [Fact]
    public async Task A_draft_that_already_carries_a_signature_is_not_signed_twice_by_the_default()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await SeedSignaturesAsync(ct);

        var run = await RunAsync(
            "draft",
            ["--to", "bob@example.org", "--subject", "Hi", "--body-file", WriteBody("Hello.\n\n-- \nAlex by hand\n")],
            ct);

        using var json = JsonDocument.Parse(run.StdOut);
        var raw = RawOf(Draft(json.RootElement));
        Assert.Equal(1, raw.Split("\n-- \n").Length - 1);
        Assert.Contains("Alex by hand", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reply_goes_out_as_the_identity_the_original_was_sent_to_with_the_signature_above_the_quote()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await SeedIdentityAsync("sales@example.test", "Sales Desk", null, ct);
        await SeedSignaturesAsync(ct);
        await ImportOriginalAsync("Sales <SALES@example.test>", "friend@example.org", ct);

        var run = await RunAsync("reply", ["1", "--no-fetch", "--body", "Thanks, done."], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.Equal("sales@example.test", json.RootElement.GetProperty("from").GetString());
        Assert.Equal("short", json.RootElement.GetProperty("signature").GetString());

        var raw = RawOf(Draft(json.RootElement));
        Assert.Contains("From: Sales Desk <sales@example.test>", raw, StringComparison.Ordinal);
        Assert.Contains("\nThanks, done.\n\n-- \nAlex\n\nOn Mon, 1 Jan 2024", raw, StringComparison.Ordinal);
        Assert.True(raw.IndexOf("-- \n", StringComparison.Ordinal) < raw.IndexOf("> Hello there.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reply_all_drops_every_own_address_from_cc_not_just_the_account_address()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await SeedIdentityAsync("sales@example.test", null, null, ct);
        await ImportOriginalAsync("Sales <sales@example.test>, Carol <carol@example.org>", "GOLDEN@example.test, friend@example.org", ct);

        var run = await RunAsync("reply", ["1", "--no-fetch", "--all", "--no-signature"], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.Equal(["carol@example.org", "friend@example.org"], json.RootElement.GetProperty("cc").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("sales@example.test", json.RootElement.GetProperty("from").GetString());
    }

    [Fact]
    public async Task Reply_with_no_text_and_no_signature_is_just_the_quote_as_before()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await ImportOriginalAsync(Own, "friend@example.org", ct);

        var run = await RunAsync("reply", ["1", "--no-fetch"], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        var raw = RawOf(Draft(json.RootElement));
        var body = raw[(raw.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        Assert.StartsWith("On Mon, 1 Jan 2024", body, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("signature").ValueKind);
    }

    [Fact]
    public async Task Account_list_names_the_identities_and_signatures()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await SeedIdentityAsync("sales@example.test", null, null, ct);
        await SeedSignaturesAsync(ct);

        var run = await RunAsync("account list", [], ct);

        using var json = JsonDocument.Parse(run.StdOut);
        var account = json.RootElement.GetProperty("accounts")[0];
        Assert.Equal(["sales@example.test"], account.GetProperty("identities").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["work", "short"], account.GetProperty("signatures").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task The_help_for_both_verbs_exists_and_names_the_unlock()
    {
        Assert.Contains(AccountIdentityCommand.OverrideEnvVar, HelpText.For("account identity"), StringComparison.Ordinal);
        Assert.Contains("--signature", HelpText.For("draft"), StringComparison.Ordinal);
        Assert.Contains("--no-signature", HelpText.For("reply"), StringComparison.Ordinal);
        Assert.Contains("RFC 3676", HelpText.For("account signature"), StringComparison.Ordinal);
        Assert.Contains("printf 'Alex Example\\nExample Pty\\n' |", HelpText.For("account signature"), StringComparison.Ordinal);
        Assert.Contains("account identity", HelpText.Overview, StringComparison.Ordinal);
        Assert.Contains("account signature", HelpText.Overview, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    private string WriteBody(string text)
    {
        var path = _workspace.PathTo("body-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, text);
        return path;
    }
}
