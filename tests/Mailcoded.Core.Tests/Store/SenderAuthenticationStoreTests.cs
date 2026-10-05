using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class SenderAuthenticationStoreTests
{
    private static RemoteEnvelope Envelope(uint uid, DmarcResult? dmarc, string? messageId = null) =>
        StoreSeed.Envelope(uid, messageId: messageId) with { Dmarc = dmarc };

    [Fact]
    public async Task The_result_round_trips_including_not_evaluated()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder,
            [
                Envelope(1, DmarcResult.Pass), Envelope(2, DmarcResult.Fail), Envelope(3, DmarcResult.None),
                Envelope(4, DmarcResult.Unknown), Envelope(5, null),
            ],
            ReferencesThreader.Instance,
            ct);

        Assert.Equal(
            [DmarcResult.Pass, DmarcResult.Fail, DmarcResult.None, DmarcResult.Unknown, null],
            Enumerable.Range(1, 5).Select(uid => Row(temp, folder, (uint)uid, ct).Dmarc));
    }

    [Fact]
    public async Task Verified_matches_only_a_recorded_pass_and_its_negation_means_no_pass_is_recorded()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder,
            [
                Envelope(1, DmarcResult.Pass), Envelope(2, DmarcResult.Fail), Envelope(3, DmarcResult.None),
                Envelope(4, DmarcResult.Unknown), Envelope(5, null),
            ],
            ReferencesThreader.Instance,
            ct);

        Assert.Equal([1u], Uids(temp, "is:verified", ct));
        Assert.Equal([2u, 3u, 4u, 5u], Uids(temp, "-is:verified", ct));
        Assert.Equal([1u, 2u, 3u, 4u, 5u], Uids(temp, "", ct));
    }

    [Fact]
    public async Task Verified_combines_with_the_other_signals()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder,
            [
                Envelope(1, DmarcResult.Pass) with { Automated = false },
                Envelope(2, DmarcResult.Pass) with { Automated = true },
                Envelope(3, DmarcResult.Fail) with { Automated = false },
                Envelope(4, null) with { Automated = false },
            ],
            ReferencesThreader.Instance,
            ct);

        Assert.Equal([1u], Uids(temp, "-is:automated is:verified", ct));
        Assert.Equal([3u, 4u], Uids(temp, "-is:automated -is:verified", ct));
    }

    [Fact]
    public async Task Search_hits_carry_the_result()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder, [Envelope(1, DmarcResult.Pass), Envelope(2, null)], ReferencesThreader.Instance, ct);

        var hits = temp.Store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse("").Query, Limit = 10 }, ct).Hits;

        Assert.Equal(DmarcResult.Pass, Assert.Single(hits, h => h.Subject == "Message 1").Dmarc);
        Assert.Null(Assert.Single(hits, h => h.Subject == "Message 2").Dmarc);
    }

    [Fact]
    public async Task A_refetched_envelope_fills_an_unrecorded_result_but_never_replaces_a_parsed_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, null)], ReferencesThreader.Instance, ct);
        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, DmarcResult.Fail)], ReferencesThreader.Instance, ct);
        Assert.Equal(DmarcResult.Fail, Row(temp, folder, 1, ct).Dmarc);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, null)], ReferencesThreader.Instance, ct);
        Assert.Equal(DmarcResult.Fail, Row(temp, folder, 1, ct).Dmarc);

        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");
        await temp.Store.SetBodyTextAsync(id, "body", null, null, new SignalValues(null, null, DmarcResult.Pass), ct);
        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, DmarcResult.Fail)], ReferencesThreader.Instance, ct);
        Assert.Equal(DmarcResult.Pass, Row(temp, folder, 1, ct).Dmarc);
    }

    [Fact]
    public async Task Storing_a_body_or_signals_without_a_result_leaves_the_recorded_one_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, DmarcResult.Pass)], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");

        await temp.Store.SetBodyTextAsync(id, "body", null, null, new SignalValues(true, false), ct);
        Assert.Equal(DmarcResult.Pass, Row(temp, folder, 1, ct).Dmarc);

        Assert.True(await temp.Store.SetSignalsAsync(id, new SignalValues(false, null), ct));
        Assert.Equal(DmarcResult.Pass, Row(temp, folder, 1, ct).Dmarc);

        Assert.True(await temp.Store.SetSignalsAsync(id, new SignalValues(null, null, DmarcResult.Unknown), ct));
        Assert.Equal(DmarcResult.Unknown, Row(temp, folder, 1, ct).Dmarc);
    }

    [Fact]
    public async Task A_reused_uid_does_not_inherit_the_previous_messages_pass()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder, [Envelope(1, DmarcResult.Pass, "first@example.com")], ReferencesThreader.Instance, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder, [Envelope(1, null, "second@example.com")], ReferencesThreader.Instance, ct);

        Assert.Null(Row(temp, folder, 1, ct).Dmarc);
        Assert.Empty(Uids(temp, "is:verified", ct));
    }

    [Fact]
    public async Task A_local_move_and_a_sync_batch_keep_the_result()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(temp.Store, account, "Archive", FolderRole.Archive, ct);

        await temp.Store.ApplySyncBatchAsync(
            new SyncBatch { FolderId = inbox, Added = [Envelope(1, DmarcResult.Pass, "moved@example.com")] },
            ReferencesThreader.Instance,
            ct);

        var id = Require.Value(temp.Store.FindMessage(inbox, new Uid(1), ct), "the message id");
        await temp.Store.MoveMessageAsync(id, archive, null, ct);

        Assert.Equal(DmarcResult.Pass, Require.Ref(temp.Store.GetEnvelope(id, ct), "the moved row").Dmarc);
    }

    [Fact]
    public async Task Bulk_ingest_stores_the_result()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await using (var bulk = await temp.Store.BeginBulkIngestAsync(ReferencesThreader.Instance, ct))
        {
            await bulk.AddAsync(folder, [Envelope(1, DmarcResult.Pass), Envelope(2, null)], ct);
            await bulk.CompleteAsync(ct);
        }

        Assert.Equal(DmarcResult.Pass, Row(temp, folder, 1, ct).Dmarc);
        Assert.Null(Row(temp, folder, 2, ct).Dmarc);
    }

    [Fact]
    public void Rows_that_predate_the_column_are_not_evaluated_and_never_verified()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        LegacyV1Database.Create(workspace.DatabasePath, "subject", "body", StoreSeed.BaseDate.ToUnixTimeMilliseconds());

        using var store = workspace.OpenStore();

        Assert.Null(Require.Ref(store.GetEnvelope(new LocalMessageId(1), ct), "the legacy row").Dmarc);
        Assert.Contains("dmarc", StoreQuery.ColumnNames(store, "messages", ct));

        Assert.Empty(store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse("is:verified").Query }, ct).Hits);
        Assert.Single(store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse("-is:verified").Query }, ct).Hits);
    }

    [Fact]
    public async Task Reading_a_cached_body_computes_the_result_once_the_account_trusts_an_authserv_id()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        using var store = workspace.OpenStore();
        var messages = Messages(store);

        var account = await store.AddAccountAsync(StoreSeed.AccountConfigFor("tester@example.com"), ct);
        var folder = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);
        var id = await CachedAsync(store, folder, 1, "046-auth-results-pass.eml", ct);

        var untrusted = await ReadAsync(messages, id, ct);
        Assert.Null(untrusted.Envelope.Dmarc);
        Assert.Null(Require.Ref(store.GetEnvelope(id, ct), "the row").Dmarc);

        await store.SetTrustedAuthservIdsAsync(account, ["mx.example.net"], ct);

        var trusted = await ReadAsync(messages, id, ct);
        Assert.Equal(DmarcResult.Pass, trusted.Envelope.Dmarc);
        Assert.Equal(DmarcResult.Pass, Require.Ref(store.GetEnvelope(id, ct), "the row").Dmarc);

        var verified = store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse("is:verified").Query }, ct).Hits;
        Assert.Single(verified);
    }

    [Fact]
    public async Task Narrowing_the_list_clears_a_pass_and_the_next_read_does_not_bring_it_back()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        using var store = workspace.OpenStore();
        var messages = Messages(store);

        var account = await store.AddAccountAsync(
            StoreSeed.AccountConfigFor("tester@example.com") with { TrustedAuthservIds = ["mx.example.net"] }, ct);
        var folder = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);
        var id = await CachedAsync(store, folder, 1, "046-auth-results-pass.eml", ct);

        Assert.Equal(DmarcResult.Pass, (await ReadAsync(messages, id, ct)).Envelope.Dmarc);

        await store.SetTrustedAuthservIdsAsync(account, ["mx.other.example.net"], ct);
        Assert.Null(Require.Ref(store.GetEnvelope(id, ct), "the row").Dmarc);

        Assert.Equal(DmarcResult.Unknown, (await ReadAsync(messages, id, ct)).Envelope.Dmarc);
        Assert.Empty(store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse("is:verified").Query }, ct).Hits);
    }

    [Fact]
    public async Task A_message_with_a_forged_pass_below_the_servers_fail_reads_as_fail()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        using var store = workspace.OpenStore();
        var messages = Messages(store);

        var account = await store.AddAccountAsync(
            StoreSeed.AccountConfigFor("tester@example.com") with { TrustedAuthservIds = ["mx.example.net"] }, ct);
        var folder = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);
        var id = await CachedAsync(store, folder, 1, "047-auth-results-forged-below.eml", ct);

        Assert.Equal(DmarcResult.Fail, (await ReadAsync(messages, id, ct)).Envelope.Dmarc);
    }

    [Fact]
    public async Task A_recorded_unknown_is_not_reparsed_on_every_read()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        using var store = workspace.OpenStore();
        var messages = Messages(store);

        var account = await store.AddAccountAsync(
            StoreSeed.AccountConfigFor("tester@example.com") with { TrustedAuthservIds = ["mx.example.net"] }, ct);
        var folder = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);
        var id = await CachedAsync(store, folder, 1, "048-auth-results-self-declared.eml", ct);

        Assert.Equal(DmarcResult.Unknown, (await ReadAsync(messages, id, ct)).Envelope.Dmarc);
        Assert.Equal(DmarcResult.Unknown, Require.Ref(store.GetEnvelope(id, ct), "the row").Dmarc);
    }

    [Fact]
    public async Task A_message_recorded_as_unknown_stays_unknown_when_the_account_later_trusts_its_header()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        using var store = workspace.OpenStore();
        var messages = Messages(store);

        var account = await store.AddAccountAsync(
            StoreSeed.AccountConfigFor("tester@example.com") with { TrustedAuthservIds = ["mx.example.net"] }, ct);
        var folder = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);

        var raw = Fixtures.Read("046-auth-results-pass.eml");
        var blob = await store.StoreBlobAsync(raw, ct);
        await store.IngestEnvelopesAsync(folder, [Envelope(1, DmarcResult.Unknown)], ReferencesThreader.Instance, ct);
        var id = Require.Value(store.FindMessage(folder, new Uid(1), ct), "the message id");
        await store.SetBodyTextAsync(id, "cached", blob.Id, false, new SignalValues(false, false), ct);

        Assert.Equal(DmarcResult.Unknown, (await ReadAsync(messages, id, ct)).Envelope.Dmarc);
    }

    [Fact]
    public async Task An_imported_message_stays_unverified_after_the_trust_list_changes_to_match_its_header()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        using var store = workspace.OpenStore();
        var messages = Messages(store);

        var account = await store.AddAccountAsync(StoreSeed.AccountConfigFor("tester@example.com"), ct);
        var folder = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);
        var blob = await store.StoreBlobAsync(Fixtures.Read("046-auth-results-pass.eml"), ct);
        await store.IngestEnvelopesAsync(folder, [Envelope(1, DmarcResult.Imported)], ReferencesThreader.Instance, ct);
        var id = Require.Value(store.FindMessage(folder, new Uid(1), ct), "the message id");
        await store.SetBodyTextAsync(id, "cached", blob.Id, false, new SignalValues(false, false, DmarcResult.Imported), ct);

        Assert.True(await store.SetTrustedAuthservIdsAsync(account, ["mx.example.net"], ct));
        Assert.Equal(DmarcResult.Imported, Require.Ref(store.GetEnvelope(id, ct), "the row").Dmarc);

        Assert.Equal("unknown", (await ReadAsync(messages, id, ct)).Envelope.Dmarc?.ToWire());
        Assert.Empty(store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse("is:verified").Query }, ct).Hits);

        await store.SetBodyTextAsync(id, "again", blob.Id, false, new SignalValues(false, false, DmarcResult.Pass), ct);
        await store.IngestEnvelopesAsync(folder, [Envelope(1, DmarcResult.Pass)], ReferencesThreader.Instance, ct);
        Assert.Equal(DmarcResult.Imported, Require.Ref(store.GetEnvelope(id, ct), "the row").Dmarc);
        Assert.Empty(store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse("is:verified").Query }, ct).Hits);
    }

    private static MessageService Messages(SqliteStore store)
    {
        var clock = new TestClock();
        return new MessageService(store, MessageParser.Default, new AuditLog(store, clock), new AgentPolicy(new AgentPolicyOptions(), clock));
    }

    private static Task<MessageView> ReadAsync(MessageService messages, LocalMessageId id, CancellationToken ct) =>
        messages.GetAsync(null, id, MessageBodyFormat.Text, fetchIfMissing: false, CallerContext.For(CallerKind.Cli, "xunit"), ct);

    private static async Task<LocalMessageId> CachedAsync(
        SqliteStore store, FolderId folder, uint uid, string fixture, CancellationToken ct)
    {
        var blob = await store.StoreBlobAsync(Fixtures.Read(fixture), ct);
        await store.IngestEnvelopesAsync(folder, [StoreSeed.Envelope(uid) with { From = "Sender Example <sender@example.com>" }], ReferencesThreader.Instance, ct);
        var id = Require.Value(store.FindMessage(folder, new Uid(uid), ct), "the message id");
        await store.SetBodyTextAsync(id, "cached", blob.Id, false, ct);
        return id;
    }

    private static async Task<FolderId> FolderAsync(TempStore temp, CancellationToken ct)
    {
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        return await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
    }

    private static EnvelopeRow Row(TempStore temp, FolderId folder, uint uid, CancellationToken ct)
    {
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(uid), ct), "the message id");
        return Require.Ref(temp.Store.GetEnvelope(id, ct), "the envelope row");
    }

    private static uint[] Uids(TempStore temp, string query, CancellationToken ct)
    {
        var hits = temp.Store.Search(
            new StoreSearchRequest { Query = SearchQueryParser.Parse(query).Query, Order = SearchOrder.Date, Limit = 50 },
            ct).Hits;

        var uids = new List<uint>();
        foreach (var hit in hits)
        {
            var row = Require.Ref(temp.Store.GetEnvelope(hit.Id, ct), "a hit row");
            uids.Add(row.Uid!.Value.Value);
        }

        uids.Sort();
        return [.. uids];
    }
}
