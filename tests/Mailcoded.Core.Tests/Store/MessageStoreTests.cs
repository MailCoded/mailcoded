using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class MessageStoreTests
{
    [Fact]
    public async Task IngestIsIdempotentOnFolderAndUid()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        RemoteEnvelope[] batch =
        [
            StoreSeed.Envelope(1, subject: "One"),
            StoreSeed.Envelope(2, subject: "Two"),
        ];

        var first = await temp.Store.IngestEnvelopesAsync(folder, batch, null, ct);
        Assert.Equal(2, first.Inserted);
        Assert.Equal(0, first.Updated);

        var second = await temp.Store.IngestEnvelopesAsync(folder, batch, null, ct);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(2, second.Updated);

        Assert.Equal(2L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM messages", ct));

        var summary = Require.Ref(temp.Store.GetFolder(folder, ct), "the folder");
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(2, summary.UnreadCount);
    }

    [Fact]
    public async Task ReIngestRefreshesTheEnvelopeAndKeepsTheSettledThreadKey()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder,
            [StoreSeed.Envelope(1, subject: "Original", messageId: "root@example.com")],
            ReferencesThreader.Instance,
            ct);

        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");
        var before = Require.Ref(temp.Store.GetEnvelope(id, ct), "the envelope");

        await temp.Store.IngestEnvelopesAsync(
            folder,
            [
                StoreSeed.Envelope(
                    1,
                    subject: "Renamed on the server",
                    messageId: "root@example.com",
                    flags: MessageFlags.None,
                    references: ["other@example.com"],
                    modSeq: 99),
            ],
            ReferencesThreader.Instance,
            ct);

        var after = Require.Ref(temp.Store.GetEnvelope(id, ct), "the refreshed envelope");
        Assert.Equal("Renamed on the server", after.Subject);
        Assert.Equal(MessageFlags.None, after.Flags);
        Assert.Equal(99ul, after.ModSeq.Value);
        Assert.Equal(before.ThreadKey, after.ThreadKey);

        var summary = Require.Ref(temp.Store.GetFolder(folder, ct), "the folder");
        Assert.Equal(1, summary.TotalCount);
        Assert.Equal(0, summary.UnreadCount);
    }

    [Fact]
    public async Task FlagChangesReplaceTheStoredBitfieldAndMoveTheUnreadCounter()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        var id = await StoreSeed.MessageAsync(
            temp.Store,
            folder,
            StoreSeed.Envelope(1, flags: MessageFlags.Unread | MessageFlags.Flagged),
            ct);

        var applied = await temp.Store.ApplyFlagChangesAsync(
            folder,
            [new FlagUpdate(new Uid(1), MessageFlags.Answered, new ModSeq(7))],
            ct);

        Assert.Equal(1, applied);

        var envelope = Require.Ref(temp.Store.GetEnvelope(id, ct), "the envelope");
        Assert.Equal(MessageFlags.Answered, envelope.Flags);
        Assert.Equal(7ul, envelope.ModSeq.Value);
        Assert.Equal(0, Require.Ref(temp.Store.GetFolder(folder, ct), "the folder").UnreadCount);

        var unknown = await temp.Store.ApplyFlagChangesAsync(
            folder,
            [new FlagUpdate(new Uid(999), MessageFlags.None, ModSeq.Zero)],
            ct);

        Assert.Equal(0, unknown);
    }

    [Fact]
    public async Task ExpungeRemovesTheRowItsBodyAndItsIndexEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(1), ct, bodyText: "first body");
        await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(2), ct, bodyText: "second body");

        var removed = await temp.Store.ExpungeAsync(folder, [new Uid(2), new Uid(404)], ct);
        Assert.Equal(1, removed);

        Assert.Null(temp.Store.FindMessage(folder, new Uid(2), ct));
        Assert.Equal(1L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM messages", ct));
        Assert.Equal(1L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM body_text", ct));
        Assert.Equal(1L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM msg_fts", ct));
        Assert.Equal(1L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM msg_fts_cjk", ct));

        var summary = Require.Ref(temp.Store.GetFolder(folder, ct), "the folder");
        Assert.Equal(1, summary.TotalCount);
        Assert.Equal(1, summary.UnreadCount);
    }

    [Fact]
    public async Task MovingAMessageRepointsItAndRebalancesBothFolders()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(temp.Store, account, "Archive", FolderRole.Archive, ct);

        var id = await StoreSeed.MessageAsync(temp.Store, inbox, StoreSeed.Envelope(1, flags: MessageFlags.Unread), ct);

        await temp.Store.MoveMessageAsync(id, archive, new Uid(500), ct);

        var moved = Require.Ref(temp.Store.GetEnvelope(id, ct), "the moved envelope");
        Assert.Equal(archive, moved.FolderId);
        Assert.Equal(500u, Require.Value(moved.Uid, "the new uid").Value);

        var source = Require.Ref(temp.Store.GetFolder(inbox, ct), "the source folder");
        Assert.Equal(0, source.TotalCount);
        Assert.Equal(0, source.UnreadCount);

        var target = Require.Ref(temp.Store.GetFolder(archive, ct), "the target folder");
        Assert.Equal(1, target.TotalCount);
        Assert.Equal(1, target.UnreadCount);
    }

    [Fact]
    public async Task MovingAnUnknownMessageIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        var failure = await Assert.ThrowsAsync<StoreException>(
            () => temp.Store.MoveMessageAsync(new LocalMessageId(9999), folder, null, ct));

        Assert.Equal(FailureCategory.NotFound, failure.Category);
    }

    [Fact]
    public async Task UidValidityFlipDropsUidsAndRelinksTheBlobByContentHash()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        var raw = "From: alice@example.com\r\nSubject: keep me\r\n\r\nbody"u8.ToArray();
        var blob = await temp.Store.StoreBlobAsync(raw, ct);

        var before = await StoreSeed.MessageAsync(
            temp.Store,
            folder,
            StoreSeed.Envelope(1, subject: "keep me", messageId: "keep@example.com"),
            ct,
            bodyText: "body");

        await temp.Store.LinkBlobAsync(before, blob.Id, ct);
        await temp.Store.SaveFolderStateAsync(
            Require.Ref(temp.Store.LoadFolderState(folder, includeKnownUids: false, ct), "the folder state")
                with { BackfillCursor = new Uid(1) },
            ct);

        var dropped = await temp.Store.InvalidateFolderUidsAsync(folder, new UidValidity(777), ct);
        Assert.Equal(1, dropped);

        var afterFlip = Require.Ref(temp.Store.GetFolder(folder, ct), "the folder");
        Assert.Equal(777u, afterFlip.UidValidity.Value);
        Assert.Equal(0, afterFlip.TotalCount);
        Assert.Equal(0, afterFlip.UnreadCount);
        Assert.Equal(0L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM messages", ct));
        Assert.Equal(1L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM blobs", ct));

        var reloaded = Require.Ref(temp.Store.LoadFolderState(folder, includeKnownUids: false, ct), "the folder state");
        Assert.Null(reloaded.BackfillCursor);

        var after = await StoreSeed.MessageAsync(
            temp.Store,
            folder,
            StoreSeed.Envelope(9001, subject: "keep me", messageId: "keep@example.com"),
            ct,
            bodyText: "body");

        var relinked = await temp.Store.TryLinkBlobBySha256Async(after, blob.Sha256, ct);
        Assert.Equal(blob.Id, Require.Value(relinked, "the relinked blob id"));
        Assert.Equal(1L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM blobs", ct));
        Assert.Equal(blob.Id, Require.Value(Require.Ref(temp.Store.GetEnvelope(after, ct), "the envelope").BlobId, "the blob link"));
    }

    [Fact]
    public async Task RelinkingContentTheStoreHasNeverSeenReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var id = await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(1), ct);

        var unknown = await temp.Store.TryLinkBlobBySha256Async(id, new string('0', 64), ct);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task SyncBatchAppliesAdditionsFlagsAndExpungesInOnePass()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(1), ct);
        await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(2), ct);

        var batch = new SyncBatch
        {
            FolderId = folder,
            Added = [StoreSeed.Envelope(3)],
            FlagChanges = [new FlagUpdate(new Uid(1), MessageFlags.None, new ModSeq(11))],
            Expunged = [new Uid(2)],
            ServerInfo = new ServerFolderInfo
            {
                Path = FolderPath.Create("INBOX"),
                UidValidity = new UidValidity(55),
                UidNext = new Uid(4),
                HighestModSeq = new ModSeq(11),
            },
        };

        var counts = await temp.Store.ApplySyncBatchAsync(batch, ReferencesThreader.Instance, ct);
        Assert.Equal(1, counts.Added);
        Assert.Equal(1, counts.Updated);
        Assert.Equal(1, counts.Expunged);

        var summary = Require.Ref(temp.Store.GetFolder(folder, ct), "the folder");
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(1, summary.UnreadCount);
        Assert.Equal(55u, summary.UidValidity.Value);

        var replayed = await temp.Store.ApplySyncBatchAsync(batch, ReferencesThreader.Instance, ct);
        Assert.Equal(0, replayed.Added);
        Assert.Equal(0, replayed.Expunged);
        Assert.Equal(2, Require.Ref(temp.Store.GetFolder(folder, ct), "the folder").TotalCount);
    }

    [Fact]
    public async Task DuplicateMessageIdsAreFoundAcrossFolders()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var sent = await StoreSeed.FolderAsync(temp.Store, account, "Sent", FolderRole.Sent, ct);

        await StoreSeed.MessageAsync(temp.Store, inbox, StoreSeed.Envelope(1, messageId: "shared@example.com"), ct);
        await StoreSeed.MessageAsync(temp.Store, sent, StoreSeed.Envelope(1, messageId: "shared@example.com"), ct);

        var copies = temp.Store.FindByMessageId(MessageId.Parse("shared@example.com"), ct);
        Assert.Equal(2, copies.Count);
    }
}
