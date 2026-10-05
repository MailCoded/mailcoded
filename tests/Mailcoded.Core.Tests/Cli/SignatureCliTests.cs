using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class SignatureCliTests : IDisposable
{
    private readonly SurfaceWorkspace _workspace = new("cli-signature");

    public void Dispose() => _workspace.Dispose();

    private SqliteStore OpenStore() =>
        new(new SqliteStoreOptions { DatabasePath = _workspace.DatabasePath }, new ManualClock());

    private async Task SeedAsync(CancellationToken ct)
    {
        using var store = OpenStore();
        await StoreSeeder.AddAccountAsync(store, ct);
    }

    private Task<CliRun> RunAsync(string[] arguments, CancellationToken ct, bool json = true) =>
        CliRunner.RunAsync(_workspace.DatabasePath, "account signature", arguments, ct, json);

    private string File(string name, string text)
    {
        var path = _workspace.PathTo(name);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    private Mailcoded.Core.Domain.Identities.SignatureBook Book()
    {
        using var store = OpenStore();
        return Require.Ref(store.GetAccount(new AccountId(1), TestContext.Current.CancellationToken), "the account").Signatures;
    }

    [Fact]
    public async Task Set_reads_a_file_cleans_it_and_audits_the_name_and_size_but_not_the_text()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await RunAsync(["set", "Work", "--file", File("sig.txt", "-- \r\nAlex Example\u202E  \r\nExample Pty\r\n\r\n")], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.True(json.RootElement.GetProperty("changed").GetBoolean());
        var signature = Assert.Single(json.RootElement.GetProperty("signatures").EnumerateArray());
        Assert.Equal("work", signature.GetProperty("name").GetString());
        Assert.Equal("Alex Example\nExample Pty", signature.GetProperty("text").GetString());

        using var store = OpenStore();
        var detail = Assert.Single(StoreQuery.Strings(store, "SELECT level || '|' || detail FROM sync_log WHERE event = 'signature_set'", ct));
        Assert.Equal("info|name=work chars=24", detail);
        Assert.DoesNotContain("Alex", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setting_the_same_text_again_changes_nothing_and_logs_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var path = File("sig.txt", "Alex");

        await RunAsync(["set", "work", "--file", path], ct);
        var again = await RunAsync(["set", "work", "--file", path], ct);

        using var json = JsonDocument.Parse(again.StdOut);
        Assert.False(json.RootElement.GetProperty("changed").GetBoolean());
        using var store = OpenStore();
        Assert.Equal(1, StoreQuery.Scalar(store, "SELECT COUNT(*) FROM sync_log WHERE event = 'signature_set'", ct));
    }

    [Theory]
    [InlineData("two words", "Alex")]
    [InlineData("a/b", "Alex")]
    [InlineData("work", "   ")]
    [InlineData("work", "--")]
    public async Task A_bad_name_or_empty_text_is_a_validation_error_and_stores_nothing(string name, string text)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var run = await RunAsync(["set", name, "--file", File("sig.txt", text)], ct);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
        Assert.True(Book().IsEmpty);
    }

    [Fact]
    public async Task A_signature_over_the_cap_is_refused_not_cut()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var tooLong = await RunAsync(["set", "work", "--file", File("long.txt", new string('x', 2_001))], ct);
        var tooMany = await RunAsync(["set", "work", "--file", File("lines.txt", string.Join('\n', Enumerable.Repeat("x", 21)))], ct);
        var enormous = await RunAsync(["set", "work", "--file", File("huge.txt", new string('x', 200_000))], ct);

        Assert.Equal(ExitCodes.Validation, tooLong.ExitCode);
        Assert.Equal(ExitCodes.Validation, tooMany.ExitCode);
        Assert.Equal(ExitCodes.Validation, enormous.ExitCode);
        Assert.True(Book().IsEmpty);
    }

    [Fact]
    public async Task Set_needs_exactly_one_source_of_text()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        Assert.Equal(ExitCodes.Validation, (await RunAsync(["set", "work"], ct)).ExitCode);
        Assert.Equal(ExitCodes.Validation, (await RunAsync(["set", "work", "--file", File("a.txt", "x"), "--stdin"], ct)).ExitCode);
        Assert.Equal(ExitCodes.NotFound, (await RunAsync(["set", "work", "--file", _workspace.PathTo("missing.txt")], ct)).ExitCode);
    }

    [Fact]
    public async Task Defaults_are_set_per_use_cleared_with_none_and_must_name_an_existing_signature()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await RunAsync(["set", "work", "--file", File("a.txt", "Alex")], ct);

        Assert.Equal(ExitCodes.Ok, (await RunAsync(["default", "new", "WORK"], ct)).ExitCode);
        Assert.Equal(ExitCodes.Ok, (await RunAsync(["default", "reply", "work"], ct)).ExitCode);
        Assert.Equal("work", Book().DefaultForNew);
        Assert.Equal("work", Book().DefaultForReply);

        Assert.Equal(ExitCodes.Ok, (await RunAsync(["default", "reply", "--none"], ct)).ExitCode);
        Assert.Equal("work", Book().DefaultForNew);
        Assert.Null(Book().DefaultForReply);

        Assert.Equal(ExitCodes.Validation, (await RunAsync(["default", "new", "missing"], ct)).ExitCode);
        Assert.Equal(ExitCodes.Validation, (await RunAsync(["default", "sideways", "work"], ct)).ExitCode);
        Assert.Equal(ExitCodes.Validation, (await RunAsync(["default", "new"], ct)).ExitCode);
        Assert.Equal("work", Book().DefaultForNew);

        using var store = OpenStore();
        Assert.Equal(3, StoreQuery.Scalar(store, "SELECT COUNT(*) FROM sync_log WHERE event = 'signature_default_changed'", ct));
    }

    [Fact]
    public async Task Forgetting_a_signature_clears_the_defaults_that_named_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await RunAsync(["set", "work", "--file", File("a.txt", "Alex")], ct);
        await RunAsync(["default", "new", "work"], ct);

        var run = await RunAsync(["forget", "WORK"], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var json = JsonDocument.Parse(run.StdOut);
        Assert.True(json.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("default_new").ValueKind);
        Assert.True(Book().IsEmpty);

        using var again = JsonDocument.Parse((await RunAsync(["forget", "work"], ct)).StdOut);
        Assert.False(again.RootElement.GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task List_shows_the_defaults_and_every_signature_in_both_forms()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await RunAsync(["set", "work", "--file", File("a.txt", "Alex\nExample Pty")], ct);
        await RunAsync(["default", "reply", "work"], ct);

        using var json = JsonDocument.Parse((await RunAsync(["list"], ct)).StdOut);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("default_new").ValueKind);
        Assert.Equal("work", json.RootElement.GetProperty("default_reply").GetString());

        var text = await RunAsync(["list"], ct, json: false);
        Assert.Contains("replies start with         work", text.StdOut, StringComparison.Ordinal);
        Assert.Contains("new messages start with    (no signature)", text.StdOut, StringComparison.Ordinal);
        Assert.Contains("    Example Pty", text.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signature_changes_need_no_unlock_because_the_text_is_in_every_preview()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        using var locked = EnvironmentScope.Set(("MAILCODED_ALLOW_IDENTITY_CHANGE", null), ("MAILCODED_SEND", null));
        Assert.Equal(ExitCodes.Ok, (await RunAsync(["set", "work", "--file", File("a.txt", "Alex")], ct)).ExitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("frobnicate")]
    [InlineData("forget")]
    [InlineData("list extra")]
    public async Task A_missing_or_unknown_action_is_a_validation_error(string line)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var arguments = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ExitCodes.Validation, (await RunAsync(arguments, ct)).ExitCode);
    }
}
