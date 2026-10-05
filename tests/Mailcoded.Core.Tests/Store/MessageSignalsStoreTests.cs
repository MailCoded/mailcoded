using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

/// <summary>Unknown (NULL) is a first-class state: it must never read as false, nor match either form of a signal query.</summary>
public sealed class MessageSignalsStoreTests
{
    private static RemoteEnvelope Envelope(uint uid, bool? automated, bool? encrypted, string? messageId = null) =>
        StoreSeed.Envelope(uid, messageId: messageId) with { Automated = automated, Encrypted = encrypted };

    [Fact]
    public async Task Signals_round_trip_through_the_envelope_row_including_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder,
            [Envelope(1, true, false), Envelope(2, false, true), Envelope(3, null, null)],
            ReferencesThreader.Instance,
            ct);

        var one = Row(temp, folder, 1, ct);
        var two = Row(temp, folder, 2, ct);
        var three = Row(temp, folder, 3, ct);

        Assert.Equal((true, false), (one.Automated, one.Encrypted));
        Assert.Equal((false, true), (two.Automated, two.Encrypted));
        Assert.Null(three.Automated);
        Assert.Null(three.Encrypted);
    }

    [Fact]
    public async Task Search_matches_known_values_and_unknown_matches_neither_form()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder,
            [Envelope(1, true, true), Envelope(2, false, false), Envelope(3, null, null)],
            ReferencesThreader.Instance,
            ct);

        Assert.Equal([1u], Uids(temp, "is:automated", ct));
        Assert.Equal([2u], Uids(temp, "-is:automated", ct));
        Assert.Equal([1u], Uids(temp, "is:encrypted", ct));
        Assert.Equal([2u], Uids(temp, "-is:encrypted", ct));
        Assert.Equal([1u, 2u, 3u], Uids(temp, "", ct));
        Assert.Equal([2u], Uids(temp, "-is:automated -is:encrypted", ct));
        Assert.Empty(Uids(temp, "is:automated -is:encrypted", ct));
    }

    [Fact]
    public async Task Search_hits_carry_the_signals_with_unknown_preserved()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder, [Envelope(1, true, false), Envelope(2, null, null)], ReferencesThreader.Instance, ct);

        var hits = temp.Store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse("").Query, Limit = 10 }, ct).Hits;

        var first = Assert.Single(hits, h => h.Subject == "Message 1");
        var second = Assert.Single(hits, h => h.Subject == "Message 2");
        Assert.Equal((true, false), (first.Automated, first.Encrypted));
        Assert.Null(second.Automated);
        Assert.Null(second.Encrypted);
    }

    [Fact]
    public async Task A_refetched_envelope_fills_an_unknown_value_but_a_null_never_erases_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, null, null)], ReferencesThreader.Instance, ct);
        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, true, false)], ReferencesThreader.Instance, ct);
        Assert.Equal((true, false), Pair(Row(temp, folder, 1, ct)));

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, null, null)], ReferencesThreader.Instance, ct);
        Assert.Equal((true, false), Pair(Row(temp, folder, 1, ct)));

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, false, true)], ReferencesThreader.Instance, ct);
        Assert.Equal((false, true), Pair(Row(temp, folder, 1, ct)));
    }

    [Fact]
    public async Task A_value_from_a_parsed_body_is_not_overwritten_by_a_later_envelope_fetch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, false, false)], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");

        await temp.Store.SetBodyTextAsync(id, "body", null, null, new SignalValues(true, true), ct);
        Assert.Equal((true, true), Pair(Row(temp, folder, 1, ct)));

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, false, false)], ReferencesThreader.Instance, ct);
        Assert.Equal((true, true), Pair(Row(temp, folder, 1, ct)));
    }

    [Fact]
    public async Task A_body_stored_without_signals_leaves_the_envelope_values_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, true, false)], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");

        await temp.Store.SetBodyTextAsync(id, "body", null, null, ct);

        Assert.Equal((true, false), Pair(Row(temp, folder, 1, ct)));
    }

    [Fact]
    public async Task A_parsed_body_upgrades_envelope_time_encrypted_false_to_true_for_inline_armor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, false, false)], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");

        var parsed = MessageParser.Default.Parse(Fixtures.Read("043-inline-pgp.eml"), StoreSeed.BaseDate, ct);
        await temp.Store.SetBodyTextAsync(id, parsed.BodyText, null, null, new SignalValues(parsed.Automated, parsed.Encrypted), ct);

        Assert.Equal((false, true), Pair(Row(temp, folder, 1, ct)));
    }

    [Fact]
    public async Task A_reused_uid_forgets_the_previous_message_signals()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, true, true, "first@example.com")], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");
        await temp.Store.SetBodyTextAsync(id, "body", null, null, new SignalValues(true, true), ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, null, null, "second@example.com")], ReferencesThreader.Instance, ct);

        var row = Row(temp, folder, 1, ct);
        Assert.Null(row.Automated);
        Assert.Null(row.Encrypted);
        Assert.False(row.BodyFetched);
    }

    [Fact]
    public async Task A_reused_uid_takes_the_new_messages_signals_when_they_are_known()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, true, true, "first@example.com")], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");
        await temp.Store.SetBodyTextAsync(id, "body", null, null, new SignalValues(true, true), ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, false, false, "second@example.com")], ReferencesThreader.Instance, ct);

        Assert.Equal((false, false), Pair(Row(temp, folder, 1, ct)));
    }

    [Fact]
    public async Task A_local_move_keeps_the_signals()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(temp.Store, account, "Archive", FolderRole.Archive, ct);

        await temp.Store.IngestEnvelopesAsync(inbox, [Envelope(1, true, false)], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(inbox, new Uid(1), ct), "the message id");

        await temp.Store.MoveMessageAsync(id, archive, null, ct);

        var moved = Require.Ref(temp.Store.GetEnvelope(id, ct), "the moved row");
        Assert.Equal(archive, moved.FolderId);
        Assert.Equal((true, false), Pair(moved));
    }

    [Fact]
    public async Task The_move_adoption_path_follows_the_same_rule()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(temp.Store, account, "Archive", FolderRole.Archive, ct);

        await temp.Store.IngestEnvelopesAsync(inbox, [Envelope(1, true, false, "moved@example.com")], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(inbox, new Uid(1), ct), "the message id");
        await temp.Store.SetBodyTextAsync(id, "body", null, null, new SignalValues(true, false), ct);
        await temp.Store.MoveMessageAsync(id, archive, null, ct);

        await temp.Store.IngestEnvelopesAsync(archive, [Envelope(9, null, null, "moved@example.com")], ReferencesThreader.Instance, ct);

        var adopted = Require.Ref(temp.Store.GetEnvelope(id, ct), "the adopted row");
        Assert.Equal(9u, adopted.Uid!.Value.Value);
        Assert.Equal((true, false), Pair(adopted));
    }

    [Fact]
    public async Task Bulk_ingest_stores_the_signals()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await using (var bulk = await temp.Store.BeginBulkIngestAsync(ReferencesThreader.Instance, ct))
        {
            await bulk.AddAsync(folder, [Envelope(1, true, false), Envelope(2, null, null)], ct);
            await bulk.CompleteAsync(ct);
        }

        Assert.Equal((true, false), Pair(Row(temp, folder, 1, ct)));
        Assert.Null(Row(temp, folder, 2, ct).Automated);
    }

    [Fact]
    public async Task A_sync_batch_stores_the_signals_in_the_same_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.ApplySyncBatchAsync(
            new SyncBatch { FolderId = folder, Added = [Envelope(1, true, true)] },
            ReferencesThreader.Instance,
            ct);

        Assert.Equal((true, true), Pair(Row(temp, folder, 1, ct)));
    }

    [Fact]
    public void Rows_that_predate_the_columns_read_as_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        LegacyV1Database.Create(workspace.DatabasePath, "subject", "body", StoreSeed.BaseDate.ToUnixTimeMilliseconds());

        using var store = workspace.OpenStore();

        var row = Require.Ref(store.GetEnvelope(new LocalMessageId(1), ct), "the legacy row");
        Assert.Null(row.Automated);
        Assert.Null(row.Encrypted);
        Assert.Contains("automated", StoreQuery.ColumnNames(store, "messages", ct));
        Assert.Contains("encrypted", StoreQuery.ColumnNames(store, "messages", ct));

        var query = SearchQueryParser.Parse("is:automated").Query;
        Assert.Empty(store.Search(new StoreSearchRequest { Query = query }, ct).Hits);
        query = SearchQueryParser.Parse("-is:automated").Query;
        Assert.Empty(store.Search(new StoreSearchRequest { Query = query }, ct).Hits);
    }

    [Fact]
    public async Task SetSignals_updates_only_what_it_is_given()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, true, false)], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");

        Assert.True(await temp.Store.SetSignalsAsync(id, new SignalValues(null, true), ct));
        Assert.Equal((true, true), Pair(Row(temp, folder, 1, ct)));

        Assert.False(await temp.Store.SetSignalsAsync(new LocalMessageId(999), new SignalValues(true, true), ct));
    }

    [Fact]
    public async Task Reading_a_cached_body_that_predates_the_signals_backfills_them_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        using var store = workspace.OpenStore();
        var clock = new TestClock();
        var messages = new MessageService(
            store, MessageParser.Default, new AuditLog(store, clock), new AgentPolicy(new AgentPolicyOptions(), clock));

        var account = await StoreSeed.AccountAsync(store, ct);
        var folder = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);

        var raw = Fixtures.Read("038-list-mail.eml");
        var blob = await store.StoreBlobAsync(raw, ct);
        await store.IngestEnvelopesAsync(folder, [Envelope(1, null, null)], ReferencesThreader.Instance, ct);
        var id = Require.Value(store.FindMessage(folder, new Uid(1), ct), "the message id");
        await store.SetBodyTextAsync(id, "cached", blob.Id, false, ct);

        Assert.Null(Require.Ref(store.GetEnvelope(id, ct), "the row").Automated);

        var view = await messages.GetAsync(
            null, id, MessageBodyFormat.Text, fetchIfMissing: false, CallerContext.For(CallerKind.Cli, "xunit"), ct);

        Assert.True(view.Envelope.Automated);
        Assert.False(view.Envelope.Encrypted);
        Assert.Equal((true, false), Pair(Require.Ref(store.GetEnvelope(id, ct), "the row")));
    }

    [Fact]
    public async Task Reading_a_cached_body_with_no_stored_raw_message_leaves_the_signals_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        using var store = workspace.OpenStore();
        var clock = new TestClock();
        var messages = new MessageService(
            store, MessageParser.Default, new AuditLog(store, clock), new AgentPolicy(new AgentPolicyOptions(), clock));

        var account = await StoreSeed.AccountAsync(store, ct);
        var folder = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);
        await store.IngestEnvelopesAsync(folder, [Envelope(1, null, null)], ReferencesThreader.Instance, ct);
        var id = Require.Value(store.FindMessage(folder, new Uid(1), ct), "the message id");
        await store.SetBodyTextAsync(id, "cached", null, false, ct);

        var view = await messages.GetAsync(
            null, id, MessageBodyFormat.Text, fetchIfMissing: false, CallerContext.For(CallerKind.Cli, "xunit"), ct);

        Assert.Null(view.Envelope.Automated);
        Assert.Null(view.Envelope.Encrypted);
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

    private static (bool?, bool?) Pair(EnvelopeRow row) => (row.Automated, row.Encrypted);

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
