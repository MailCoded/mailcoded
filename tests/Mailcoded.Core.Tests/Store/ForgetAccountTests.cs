using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class ForgetAccountTests
{
    /// <summary>
    /// The FTS indexes are contentless, so nothing cascades into them. Without an explicit clear,
    /// every later search returns hits for rows that no longer exist.
    /// </summary>
    [Fact]
    public async Task Forgetting_an_account_leaves_no_orphaned_search_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = TempStore.Create();

        var accountId = await StoreSeed.AccountAsync(store.Store, ct, "gone@example.com");
        var folderId = await StoreSeed.FolderAsync(store.Store, accountId, "INBOX", FolderRole.Inbox, ct);
        await SeedAsync(store.Store, folderId, 5, ct);

        Assert.Equal(5L, Count(store.Store, "messages", ct));
        Assert.Equal(5L, Count(store.Store, "msg_fts", ct));

        var removed = await store.Store.ForgetAccountAsync(accountId, ct);

        Assert.True(removed.Existed);
        Assert.Equal(5, removed.Messages);
        Assert.Equal(0L, Count(store.Store, "accounts", ct));
        Assert.Equal(0L, Count(store.Store, "messages", ct));
        Assert.Equal(0L, Count(store.Store, "msg_fts", ct));
        Assert.Equal(0L, Count(store.Store, "msg_fts_cjk", ct));
        Assert.Equal(0L, Count(store.Store, "body_text", ct));
    }

    [Fact]
    public async Task Forgetting_one_account_leaves_the_other_intact()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = TempStore.Create();

        var doomed = await StoreSeed.AccountAsync(store.Store, ct, "doomed@example.com");
        var doomedFolder = await StoreSeed.FolderAsync(store.Store, doomed, "INBOX", FolderRole.Inbox, ct);
        await SeedAsync(store.Store, doomedFolder, 3, ct);

        var kept = await StoreSeed.AccountAsync(store.Store, ct, "kept@example.com");
        var keptFolder = await StoreSeed.FolderAsync(store.Store, kept, "Archive", FolderRole.Archive, ct);
        await SeedAsync(store.Store, keptFolder, 4, ct);

        await store.Store.ForgetAccountAsync(doomed, ct);

        Assert.Equal(1L, Count(store.Store, "accounts", ct));
        Assert.Equal(4L, Count(store.Store, "messages", ct));
        Assert.Equal(4L, Count(store.Store, "msg_fts", ct));
        Assert.NotNull(store.Store.GetAccount(kept, ct));
    }

    [Fact]
    public async Task Forgetting_an_account_that_is_already_gone_is_reported_not_thrown()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = TempStore.Create();

        var removed = await store.Store.ForgetAccountAsync(new AccountId(4242), ct);

        Assert.False(removed.Existed);
        Assert.Equal(0, removed.Messages);
    }

    private static long Count(SqliteStore store, string table, CancellationToken ct) =>
        StoreQuery.Scalar(store, "SELECT count(*) FROM " + table, ct);

    private static async Task SeedAsync(SqliteStore store, FolderId folderId, int count, CancellationToken ct)
    {
        for (var i = 1; i <= count; i++)
        {
            await StoreSeed.MessageAsync(
                store,
                folderId,
                StoreSeed.Envelope(uid: (uint)i, subject: $"quarterly report {i}"),
                ct,
                bodyText: $"body text number {i}");
        }
    }
}
