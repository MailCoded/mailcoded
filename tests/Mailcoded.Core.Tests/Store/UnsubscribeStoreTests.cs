using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

/// <summary>Null is unknown, empty is "headers seen, nothing offered"; the stored form is parsed on read.</summary>
public sealed class UnsubscribeStoreTests
{
    private const string Header = "<mailto:leave@example.org?subject=unsubscribe>, <https://lists.example.org/u/abc>";

    private static RemoteEnvelope Envelope(uint uid, SignalHeaders? headers, string? messageId = null) =>
        StoreSeed.Envelope(uid, messageId: messageId) with { Headers = headers };

    private static SignalHeaders WithList(string value, string? post = null) =>
        new() { ListUnsubscribe = value, ListUnsubscribePost = post };

    [Fact]
    public async Task The_value_round_trips_and_distinguishes_unknown_from_none()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder,
            [
                Envelope(1, WithList(Header, "List-Unsubscribe=One-Click")),
                Envelope(2, SignalHeaders.None),
                Envelope(3, null),
            ],
            ReferencesThreader.Instance,
            ct);

        var offered = Require.Ref(UnsubscribeStorage.Decode(Row(temp, folder, 1, ct).Unsubscribe), "options");
        Assert.Equal("leave@example.org", offered.Mailto?.Address);
        Assert.Equal(["https://lists.example.org/u/abc"], offered.Web);
        Assert.True(offered.OneClick);

        Assert.Equal(string.Empty, Row(temp, folder, 2, ct).Unsubscribe);
        Assert.Null(Row(temp, folder, 3, ct).Unsubscribe);
    }

    [Fact]
    public async Task A_refetched_envelope_fills_unknown_and_never_erases()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, null)], ReferencesThreader.Instance, ct);
        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, WithList(Header))], ReferencesThreader.Instance, ct);
        Assert.Equal(Header, Row(temp, folder, 1, ct).Unsubscribe);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, null)], ReferencesThreader.Instance, ct);
        Assert.Equal(Header, Row(temp, folder, 1, ct).Unsubscribe);
    }

    [Fact]
    public async Task A_parsed_body_is_not_overwritten_by_a_later_envelope_fetch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, SignalHeaders.None)], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");

        await temp.Store.SetBodyTextAsync(id, "body", null, null, new SignalValues(null, null, null, Header), ct);
        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, SignalHeaders.None)], ReferencesThreader.Instance, ct);

        Assert.Equal(Header, Row(temp, folder, 1, ct).Unsubscribe);
    }

    [Fact]
    public async Task A_body_stored_without_signals_leaves_the_envelope_value_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Envelope(1, WithList(Header))], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");

        await temp.Store.SetBodyTextAsync(id, "body", null, null, ct);

        Assert.Equal(Header, Row(temp, folder, 1, ct).Unsubscribe);
    }

    [Fact]
    public async Task SetSignals_records_the_value_and_leaves_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder, [Envelope(1, null) with { Automated = true }], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");

        Assert.True(await temp.Store.SetSignalsAsync(id, new SignalValues(null, null, null, Header), ct));

        var row = Row(temp, folder, 1, ct);
        Assert.Equal(Header, row.Unsubscribe);
        Assert.True(row.Automated);
    }

    [Fact]
    public async Task A_reused_uid_forgets_the_previous_messages_unsubscribe_headers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder, [Envelope(1, WithList(Header), "first@example.com")], ReferencesThreader.Instance, ct);
        await temp.Store.IngestEnvelopesAsync(
            folder, [Envelope(1, null, "second@example.com")], ReferencesThreader.Instance, ct);

        Assert.Null(Row(temp, folder, 1, ct).Unsubscribe);
    }

    [Fact]
    public async Task A_local_move_keeps_the_value()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(temp.Store, account, "Archive", FolderRole.Archive, ct);

        await temp.Store.IngestEnvelopesAsync(inbox, [Envelope(1, WithList(Header))], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(inbox, new Uid(1), ct), "the message id");
        await temp.Store.MoveMessageAsync(id, archive, null, ct);

        Assert.Equal(Header, Require.Ref(temp.Store.GetEnvelope(id, ct), "the moved row").Unsubscribe);
    }

    [Fact]
    public async Task A_sync_batch_stores_the_value_in_the_same_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.ApplySyncBatchAsync(
            new SyncBatch { FolderId = folder, Added = [Envelope(1, WithList(Header))] },
            ReferencesThreader.Instance,
            ct);

        Assert.Equal(Header, Row(temp, folder, 1, ct).Unsubscribe);
    }

    [Fact]
    public void Rows_that_predate_the_column_read_as_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        LegacyV1Database.Create(workspace.DatabasePath, "subject", "body", StoreSeed.BaseDate.ToUnixTimeMilliseconds());

        using var store = workspace.OpenStore();

        Assert.Null(Require.Ref(store.GetEnvelope(new LocalMessageId(1), ct), "the legacy row").Unsubscribe);
        Assert.Contains("unsubscribe", StoreQuery.ColumnNames(store, "messages", ct));
    }

    [Fact]
    public async Task Reading_a_cached_body_with_unknown_headers_backfills_them_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        using var store = workspace.OpenStore();
        var messages = Messages(store);

        var account = await StoreSeed.AccountAsync(store, ct);
        var folder = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);

        var blob = await store.StoreBlobAsync(Fixtures.Read("050-unsubscribe-mailto.eml"), ct);
        await store.IngestEnvelopesAsync(
            folder, [Envelope(1, null) with { Automated = true, Encrypted = false }], ReferencesThreader.Instance, ct);
        var id = Require.Value(store.FindMessage(folder, new Uid(1), ct), "the message id");
        await store.SetBodyTextAsync(id, "cached", blob.Id, false, new SignalValues(true, false), ct);

        Assert.Null(Require.Ref(store.GetEnvelope(id, ct), "the row").Unsubscribe);

        var view = await messages.GetAsync(
            null, id, MessageBodyFormat.Text, fetchIfMissing: false, CallerContext.For(CallerKind.Cli, "xunit"), ct);

        var options = Require.Ref(UnsubscribeStorage.Decode(view.Envelope.Unsubscribe), "options");
        Assert.Equal("leave-9f3a@bounce.club.example.org", options.Mailto?.Address);
        Assert.Equal("Unsubscribe me", options.Mailto?.Subject);
        Assert.Equal("Please remove this address", options.Mailto?.Body);
        Assert.NotNull(Require.Ref(store.GetEnvelope(id, ct), "the row").Unsubscribe);
    }

    [Fact]
    public async Task A_cached_body_without_the_headers_is_recorded_as_none_so_it_is_not_reparsed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        using var store = workspace.OpenStore();
        var messages = Messages(store);

        var account = await StoreSeed.AccountAsync(store, ct);
        var folder = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);

        var blob = await store.StoreBlobAsync(Fixtures.Read("001-plain-simple.eml"), ct);
        await store.IngestEnvelopesAsync(
            folder, [Envelope(1, null) with { Automated = false, Encrypted = false }], ReferencesThreader.Instance, ct);
        var id = Require.Value(store.FindMessage(folder, new Uid(1), ct), "the message id");
        await store.SetBodyTextAsync(id, "cached", blob.Id, false, new SignalValues(false, false), ct);

        await messages.GetAsync(
            null, id, MessageBodyFormat.Text, fetchIfMissing: false, CallerContext.For(CallerKind.Cli, "xunit"), ct);

        Assert.Equal(string.Empty, Require.Ref(store.GetEnvelope(id, ct), "the row").Unsubscribe);
    }

    private static MessageService Messages(Mailcoded.Core.Store.SqliteStore store)
    {
        var clock = new TestClock();
        return new MessageService(store, MessageParser.Default, new AuditLog(store, clock), new AgentPolicy(new AgentPolicyOptions(), clock));
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
}
