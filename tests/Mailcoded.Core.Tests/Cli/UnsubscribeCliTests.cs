using System.Text;
using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class UnsubscribeCliTests : IDisposable
{
    private readonly SurfaceWorkspace _workspace = new("cli-unsubscribe");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task A_mailto_target_becomes_an_ordinary_draft_and_nothing_is_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        await ImportAsync(ct, "050-unsubscribe-mailto.eml");

        using var gate = EnvironmentScope.Set(("MAILCODED_SEND", null), ("MAILCODED_APPROVED_RECIPIENTS", null));
        var run = await RunAsync("unsubscribe", ["1"], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var document = JsonDocument.Parse(run.StdOut);
        var root = document.RootElement;

        Assert.True(root.GetProperty("drafted").GetBoolean());
        Assert.False(root.GetProperty("sent").GetBoolean());
        Assert.False(root.TryGetProperty("confirm_token", out _));
        Assert.False(root.GetProperty("send").GetProperty("enabled").GetBoolean());
        Assert.StartsWith("mailcoded send-preview ", root.GetProperty("next").GetString(), StringComparison.Ordinal);
        Assert.Equal("leave-9f3a@bounce.club.example.org", root.GetProperty("to")[0].GetString());

        var draft = Draft(root.GetProperty("draft_id").GetInt64());
        Assert.Equal(Mailcoded.Core.Domain.Outbox.OutboxState.Queued, draft.State);
        Assert.False(HasLiveConfirmToken(draft.Id));
        Assert.Equal(["leave-9f3a@bounce.club.example.org"], draft.Envelope!.To.Select(a => a.Value).ToArray());
        Assert.Empty(draft.Envelope.Cc);
        Assert.Empty(draft.Envelope.Bcc);

        var raw = Encoding.UTF8.GetString(draft.Raw);
        Assert.Contains("Subject: Unsubscribe me", raw, StringComparison.Ordinal);
        Assert.Contains("Please remove this address", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_request_comes_from_the_identity_the_list_mail_was_addressed_to()
    {
        var ct = TestContext.Current.CancellationToken;
        await ImportAsync(ct, "050-unsubscribe-mailto.eml");
        using (var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = _workspace.DatabasePath }, new ManualClock()))
        {
            Assert.True(SenderIdentity.TryCreate("bob@example.org", null, null, out var identity, out var error), error);
            await store.EditIdentitiesAsync(new AccountId(1), list => SenderIdentities.With(list, identity), ct);
        }

        var run = await RunAsync("unsubscribe", ["1"], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var document = JsonDocument.Parse(run.StdOut);
        var draft = Draft(document.RootElement.GetProperty("draft_id").GetInt64());
        var raw = Encoding.UTF8.GetString(draft.Raw);

        Assert.Contains("<bob@example.org>", raw.Split('\n').Single(l => l.StartsWith("From:", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain("In-Reply-To", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("References", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_send_gate_still_refuses_an_address_the_agent_was_not_approved_for()
    {
        var ct = TestContext.Current.CancellationToken;
        await ImportAsync(ct, "050-unsubscribe-mailto.eml");

        using var gate = EnvironmentScope.Set(
            ("MAILCODED_SEND", "1"), ("MAILCODED_APPROVED_RECIPIENTS", "boss@example.org"));
        var run = await RunAsync("unsubscribe", ["1"], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var document = JsonDocument.Parse(run.StdOut);
        var send = document.RootElement.GetProperty("send");
        Assert.False(send.GetProperty("enabled").GetBoolean());
        Assert.NotEqual("allowed", send.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Only_the_address_subject_and_body_survive_from_a_hostile_header()
    {
        var ct = TestContext.Current.CancellationToken;
        await ImportAsync(ct, "052-unsubscribe-hostile.eml");

        var run = await RunAsync("unsubscribe", ["1"], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var document = JsonDocument.Parse(run.StdOut);
        var draft = Draft(document.RootElement.GetProperty("draft_id").GetInt64());

        Assert.Equal(["d@example.net"], draft.Envelope!.To.Select(a => a.Value).ToArray());
        Assert.Empty(draft.Envelope.Cc);
        Assert.Empty(draft.Envelope.Bcc);

        var raw = Encoding.UTF8.GetString(draft.Raw);
        Assert.Contains("Subject: Leave list", raw, StringComparison.Ordinal);
        foreach (var leaked in new[] { "victim@example.org", "e@example.net", "f@example.net", "g@example.net", "h@example.net", "evil.example.com" })
            Assert.DoesNotContain(leaked, raw, StringComparison.Ordinal);

        Assert.Empty(document.RootElement.GetProperty("unsubscribe").GetProperty("web").EnumerateArray());
    }

    [Fact]
    public async Task A_web_only_message_prints_the_links_as_not_opened_and_drafts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await ImportAsync(ct, "051-unsubscribe-https-one-click.eml");

        var run = await RunAsync("unsubscribe", ["1"], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var document = JsonDocument.Parse(run.StdOut);
        var root = document.RootElement;

        Assert.False(root.GetProperty("drafted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("draft_id").ValueKind);
        var unsubscribe = root.GetProperty("unsubscribe");
        Assert.True(unsubscribe.GetProperty("one_click").GetBoolean());
        Assert.Equal("https://shop.example.com/u/7c1e5", unsubscribe.GetProperty("web")[0].GetString());
        Assert.Contains("never opens", unsubscribe.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Empty(OutboxIds());
    }

    [Fact]
    public async Task The_human_output_says_the_links_were_not_opened_and_names_the_next_steps()
    {
        var ct = TestContext.Current.CancellationToken;
        await ImportAsync(ct, "038-list-mail.eml");

        var run = await CliRunner.RunAsync(_workspace.DatabasePath, "unsubscribe", ["1"], ct, json: false);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        Assert.Contains("Nothing has been sent", run.StdOut, StringComparison.Ordinal);
        Assert.Contains("mailcoded send-preview", run.StdOut, StringComparison.Ordinal);
        Assert.Contains("send-draft", run.StdOut, StringComparison.Ordinal);
        Assert.Contains("NOT opened", run.StdOut, StringComparison.Ordinal);
        Assert.Contains("https://lists.example.net/unsubscribe/abc123", run.StdOut, StringComparison.Ordinal);
        Assert.Contains("one-click", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_message_with_no_unsubscribe_headers_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        await ImportAsync(ct, "001-plain-simple.eml");

        var run = await RunAsync("unsubscribe", ["1"], ct);

        Assert.Equal(ExitCodes.NotFound, run.ExitCode);
        Assert.Empty(OutboxIds());
    }

    [Fact]
    public async Task An_unknown_header_set_with_no_cached_body_and_no_fetch_asks_for_a_sync()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = _workspace.DatabasePath }, new ManualClock()))
        {
            var account = await StoreSeeder.AddAccountAsync(store, ct);
            var folder = await StoreSeeder.AddInboxAsync(store, account, ct);
            await store.IngestEnvelopesAsync(
                folder, [StoreSeed.Envelope(1)], ReferencesThreader.Instance, ct);
        }

        var run = await RunAsync("unsubscribe", ["1", "--no-fetch"], ct);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
        Assert.Empty(OutboxIds());
    }

    [Fact]
    public async Task Headers_stored_at_sync_time_need_no_body_and_no_server()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = _workspace.DatabasePath }, new ManualClock()))
        {
            var account = await StoreSeeder.AddAccountAsync(store, ct);
            var folder = await StoreSeeder.AddInboxAsync(store, account, ct);
            await store.IngestEnvelopesAsync(
                folder,
                [
                    StoreSeed.Envelope(1) with
                    {
                        Headers = new SignalHeaders { ListUnsubscribe = "<mailto:leave@example.org>" },
                    },
                ],
                ReferencesThreader.Instance,
                ct);
        }

        var run = await RunAsync("unsubscribe", ["1"], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var document = JsonDocument.Parse(run.StdOut);
        Assert.True(document.RootElement.GetProperty("drafted").GetBoolean());
    }

    [Fact]
    public async Task Read_reports_the_options_and_the_not_opened_note_and_null_when_there_are_none()
    {
        var ct = TestContext.Current.CancellationToken;
        await ImportAsync(ct, "001-plain-simple.eml", "038-list-mail.eml");

        using var listed = JsonDocument.Parse((await RunAsync("read", ["2", "--no-fetch"], ct)).StdOut);
        var unsubscribe = listed.RootElement.GetProperty("unsubscribe");
        Assert.Equal("unsubscribe@lists.example.net", unsubscribe.GetProperty("mailto").GetProperty("address").GetString());
        Assert.Equal("unsubscribe", unsubscribe.GetProperty("mailto").GetProperty("subject").GetString());
        Assert.Equal(1, unsubscribe.GetProperty("web").GetArrayLength());
        Assert.True(unsubscribe.GetProperty("one_click").GetBoolean());
        Assert.Contains("do not open", unsubscribe.GetProperty("note").GetString(), StringComparison.Ordinal);

        using var plain = JsonDocument.Parse((await RunAsync("read", ["1", "--no-fetch"], ct)).StdOut);
        Assert.Equal(JsonValueKind.Null, plain.RootElement.GetProperty("unsubscribe").ValueKind);
    }

    [Fact]
    public async Task The_verb_takes_exactly_one_message_id()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(ExitCodes.Validation, (await RunAsync("unsubscribe", [], ct)).ExitCode);
        Assert.Equal(ExitCodes.Validation, (await RunAsync("unsubscribe", ["1", "2"], ct)).ExitCode);
        Assert.Equal(ExitCodes.Validation, (await RunAsync("unsubscribe", ["abc"], ct)).ExitCode);
    }

    private async Task ImportAsync(CancellationToken ct, params string[] fixtures)
    {
        var directory = _workspace.PathTo("eml");
        Directory.CreateDirectory(directory);
        foreach (var name in fixtures) File.WriteAllBytes(Path.Combine(directory, name), Fixtures.Read(name));

        Assert.Equal(ExitCodes.Ok, (await RunAsync("import-eml", [directory], ct)).ExitCode);
    }

    private Task<CliRun> RunAsync(string verb, string[] arguments, CancellationToken ct) =>
        CliRunner.RunAsync(_workspace.DatabasePath, verb, arguments, ct);

    private OutboxRecord Draft(long id)
    {
        using var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = _workspace.DatabasePath }, new ManualClock());
        return Require.Ref(store.GetOutbox(id, TestContext.Current.CancellationToken), "the draft");
    }

    private bool HasLiveConfirmToken(long draftId)
    {
        using var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = _workspace.DatabasePath }, new ManualClock());
        return store.HasConfirmToken(draftId, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    }

    private IReadOnlyList<long> OutboxIds()
    {
        using var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = _workspace.DatabasePath }, new ManualClock());
        return [.. store.ListOutbox(null, TestContext.Current.CancellationToken).Select(r => r.Id)];
    }
}
