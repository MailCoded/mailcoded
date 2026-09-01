using System.Text;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

/// <summary>RELIABILITY §14.5 case 3: the message occupying a UID is identified by its content,
/// not by the UID, so a local move and a reused UID both reconcile instead of duplicating.</summary>
public sealed class UidReconciliationTests
{
    private const string MovedMessageId = "moved@example.com";

    [Fact]
    public async Task AMoveWithoutCopyuidReconcilesWithTheServerCopyInsteadOfDuplicating()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(temp.Store, account, "Archive", FolderRole.Archive, ct);

        var id = await StoreSeed.MessageAsync(
            temp.Store,
            inbox,
            StoreSeed.Envelope(5, subject: "Contract", messageId: MovedMessageId),
            ct,
            bodyText: "zebracrossing contract terms",
            tags: StoreSeed.Tags("todo"));

        // No UIDPLUS, so the server's COPYUID is unknown and the row lands with uid NULL.
        await temp.Store.MoveMessageAsync(id, archive, null, ct);

        var result = await temp.Store.IngestEnvelopesAsync(
            archive,
            [StoreSeed.Envelope(77, subject: "Contract", messageId: MovedMessageId)],
            null,
            ct);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);

        Assert.True(
            StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM messages", ct) == 1,
            "The synced copy must adopt the uid-NULL row the move left behind. A second row would show the message "
            + "twice, keep body, blob and tags on the invisible copy, and leave total_count permanently high.");

        Assert.Equal(id, Require.Value(temp.Store.FindMessage(archive, new Uid(77), ct), "the adopted row"));
        Assert.Equal("zebracrossing contract terms", temp.Store.GetBodyText(id, ct));
        Assert.True(temp.Store.IsBodyFetched(id, ct));
        var tags = temp.Store.GetTags(id, ct);
        Assert.Single(tags);
        Assert.Equal("todo", tags[0].Value);

        var folder = Require.Ref(temp.Store.GetFolder(archive, ct), "the target folder");
        Assert.Equal(1, folder.TotalCount);
    }

    [Fact]
    public async Task AnUnrelatedMessageIsNeverAdoptedByAMovedRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(temp.Store, account, "Archive", FolderRole.Archive, ct);

        var id = await StoreSeed.MessageAsync(
            temp.Store,
            inbox,
            StoreSeed.Envelope(5, messageId: MovedMessageId),
            ct);

        await temp.Store.MoveMessageAsync(id, archive, null, ct);

        var result = await temp.Store.IngestEnvelopesAsync(
            archive,
            [StoreSeed.Envelope(77, messageId: "someone-else@example.com")],
            null,
            ct);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(2L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM messages", ct));
        Assert.Null(temp.Store.GetEnvelope(id, ct)?.Uid);
    }

    [Fact]
    public async Task AReusedUidDropsThePreviousMessagesBodyBlobAndIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        var id = await StoreSeed.MessageAsync(
            temp.Store,
            inbox,
            StoreSeed.Envelope(42, subject: "Payroll", messageId: "old@example.com"),
            ct);

        var blob = await temp.Store.StoreBlobAsync(Encoding.UTF8.GetBytes("From: old\r\n\r\nzebracrossing payroll"), ct);
        await temp.Store.SetBodyTextAsync(id, "zebracrossing payroll", blob.Id, false, ct);

        // Same UIDVALIDITY, same UID, different message: the server reused the slot.
        await temp.Store.IngestEnvelopesAsync(
            inbox,
            [StoreSeed.Envelope(42, subject: "Lunch", messageId: "new@example.com")],
            null,
            ct);

        var row = Require.Ref(temp.Store.GetEnvelope(id, ct), "the row occupying uid 42");

        Assert.Equal("Lunch", row.Subject);
        Assert.True(
            !row.BodyFetched && row.BlobId is null && temp.Store.GetBodyText(id, ct) is null,
            "With body_fetched still set, the lazy fetch is skipped and the previous message's plaintext is served "
            + "under the new message's headers.");

        Assert.Empty(Search(temp.Store, "zebracrossing", ct).Hits);

        Assert.True(
            StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM sync_log WHERE event = 'uid_reused'", ct) == 1,
            "Edge case 3 says to log the quirk: a server reusing UIDs is the reason a later mismatch is not a bug.");
    }

    [Fact]
    public async Task RefetchingTheSameMessageKeepsItsBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        var id = await StoreSeed.MessageAsync(
            temp.Store,
            inbox,
            StoreSeed.Envelope(42, subject: "Payroll", messageId: "same@example.com"),
            ct,
            bodyText: "zebracrossing payroll");

        await temp.Store.IngestEnvelopesAsync(
            inbox,
            [StoreSeed.Envelope(42, subject: "Payroll", messageId: "same@example.com", flags: MessageFlags.None)],
            null,
            ct);

        Assert.Equal("zebracrossing payroll", temp.Store.GetBodyText(id, ct));
        Assert.True(temp.Store.IsBodyFetched(id, ct));
        Assert.Single(Search(temp.Store, "zebracrossing", ct).Hits);
        Assert.Equal(0L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM sync_log WHERE event = 'uid_reused'", ct));
    }

    private static StoreSearchResult Search(SqliteStore store, string query, CancellationToken ct) =>
        store.Search(
            new StoreSearchRequest
            {
                Query = SearchQueryParser.Parse(query).Query,
                Order = SearchOrder.Relevance,
                Limit = 50,
            },
            ct);
}
