using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class FolderStoreTests
{
    [Fact]
    public async Task UpsertIsIdempotentAndNeverForgetsAKnownRole()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var first = await temp.Store.UpsertFoldersAsync(
            account,
            [
                new RemoteFolder { Path = FolderPath.Create("INBOX"), Role = FolderRole.Inbox },
                new RemoteFolder { Path = FolderPath.Create("Archive/2026"), Role = FolderRole.Archive },
            ],
            ct);

        var second = await temp.Store.UpsertFoldersAsync(
            account,
            [
                new RemoteFolder { Path = FolderPath.Create("INBOX"), Role = FolderRole.None },
                new RemoteFolder { Path = FolderPath.Create("Archive/2026"), Role = FolderRole.Archive },
            ],
            ct);

        Assert.Equal(first, second);

        var folders = temp.Store.ListFolders(account, ct);
        Assert.Equal(2, folders.Count);
        Assert.Equal("Archive/2026", folders[0].Path.Value);
        Assert.Equal("INBOX", folders[1].Path.Value);
        Assert.Equal(FolderRole.Inbox, folders[1].Role);
    }

    [Fact]
    public async Task ServerFolderInfoIsPersistedWithoutTouchingLocalCounters()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(1), ct);

        await temp.Store.SaveServerFolderInfoAsync(
            folder,
            new ServerFolderInfo
            {
                Path = FolderPath.Create("INBOX"),
                UidValidity = new UidValidity(90210),
                UidNext = new Uid(77),
                HighestModSeq = new ModSeq(4242),
                Role = FolderRole.Inbox,
                TotalCount = 9999,
                UnreadCount = 8888,
                PermanentFlagsAllowCustomKeywords = false,
            },
            ct);

        var summary = Require.Ref(temp.Store.GetFolder(folder, ct), "the folder");
        Assert.Equal(90210u, summary.UidValidity.Value);
        Assert.Equal(77u, Require.Value(summary.UidNext, "uidnext").Value);
        Assert.Equal(4242ul, summary.HighestModSeq.Value);

        // The server's own counts describe its mailbox, not what is stored here.
        Assert.Equal(1, summary.TotalCount);
        Assert.Equal(1, summary.UnreadCount);

        var state = Require.Ref(temp.Store.LoadFolderState(folder, includeKnownUids: false, ct), "the folder state");
        Assert.False(state.ServerAcceptsCustomKeywords);
    }

    [Fact]
    public async Task FolderStateRoundTripsAndKeepsTheDiffAnchor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(3), ct);
        await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(9), ct);

        var anchor = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
        var loaded = Require.Ref(temp.Store.LoadFolderState(folder, includeKnownUids: true, ct), "the folder state");

        Assert.Equal(new[] { 3u, 9u }, Uids(Require.Ref(loaded.KnownUids, "the known uid list")));
        Assert.Equal(9u, Require.Value(loaded.HighestKnownUid, "the highest known uid").Value);

        await temp.Store.SaveFolderStateAsync(
            loaded with
            {
                UidValidity = new UidValidity(1234),
                HighestModSeq = new ModSeq(88),
                BackfillCursor = new Uid(3),
                ServerAcceptsCustomKeywords = false,
                LastFullDiffUtc = anchor,
            },
            ct);

        var reloaded = Require.Ref(temp.Store.LoadFolderState(folder, includeKnownUids: false, ct), "the reloaded state");
        Assert.Equal(1234u, reloaded.UidValidity.Value);
        Assert.Equal(88ul, reloaded.HighestModSeq.Value);
        Assert.Equal(3u, Require.Value(reloaded.BackfillCursor, "the backfill cursor").Value);
        Assert.False(reloaded.ServerAcceptsCustomKeywords);
        Assert.Equal(anchor, Require.Value(reloaded.LastFullDiffUtc, "the diff anchor"));

        await temp.Store.SaveFolderStateAsync(reloaded with { LastFullDiffUtc = null }, ct);

        var afterNullAnchor = Require.Ref(temp.Store.LoadFolderState(folder, includeKnownUids: false, ct), "the state");
        Assert.Equal(anchor, Require.Value(afterNullAnchor.LastFullDiffUtc, "the preserved diff anchor"));
    }

    [Fact]
    public async Task DeltaTokenRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await temp.Store.SetDeltaTokenAsync(folder, "delta-token-1", ct);
        Assert.Equal("delta-token-1", Require.Ref(temp.Store.GetFolder(folder, ct), "the folder").DeltaToken);

        await temp.Store.SetDeltaTokenAsync(folder, null, ct);
        Assert.Null(Require.Ref(temp.Store.GetFolder(folder, ct), "the folder").DeltaToken);
    }

    [Fact]
    public async Task CountersTrackIngestedRowsAndSurviveARecount()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(1, flags: MessageFlags.Unread), ct);
        await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(2, flags: MessageFlags.None), ct);
        await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(3, flags: MessageFlags.Unread), ct);

        var summary = Require.Ref(temp.Store.GetFolder(folder, ct), "the folder");
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(2, summary.UnreadCount);

        await temp.Store.RecountAllFoldersAsync(ct);

        var recounted = Require.Ref(temp.Store.GetFolder(folder, ct), "the folder");
        Assert.Equal(3, recounted.TotalCount);
        Assert.Equal(2, recounted.UnreadCount);
    }

    [Fact]
    public async Task RemovingAFolderTakesItsMessagesAndIndexRowsWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var kept = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var dropped = await StoreSeed.FolderAsync(temp.Store, account, "Gone", FolderRole.None, ct);

        await StoreSeed.MessageAsync(temp.Store, kept, StoreSeed.Envelope(1, subject: "kept"), ct, bodyText: "kept body");
        await StoreSeed.MessageAsync(temp.Store, dropped, StoreSeed.Envelope(1, subject: "vanishing"), ct, bodyText: "vanishing body");

        await temp.Store.RemoveFolderAsync(dropped, ct);

        Assert.Null(temp.Store.GetFolder(dropped, ct));
        Assert.Single(temp.Store.ListFolders(account, ct));
        Assert.Equal(1L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM messages", ct));
        Assert.Equal(1L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM msg_fts", ct));
        Assert.Equal(1L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM msg_fts_cjk", ct));
    }

    private static uint[] Uids(IReadOnlyList<Uid> uids)
    {
        var values = new uint[uids.Count];
        for (var i = 0; i < uids.Count; i++) values[i] = uids[i].Value;
        return values;
    }
}
