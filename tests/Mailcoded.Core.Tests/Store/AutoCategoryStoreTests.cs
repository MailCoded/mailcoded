using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class AutoCategoryStoreTests
{
    private static readonly Tag Social = Tag.Parse("auto.social");
    private static readonly Tag News = Tag.Parse("auto.news");
    private static readonly Tag Marketing = Tag.Parse("auto.marketing");

    private static RemoteEnvelope Arrival(
        uint uid,
        string from = "Ada <ada@example.com>",
        string? subject = null,
        string? messageId = null,
        bool? automated = null,
        string? listId = null) =>
        StoreSeed.Envelope(uid, subject: subject, from: from, messageId: messageId) with
        {
            Automated = automated,
            Headers = listId is null ? null : new SignalHeaders { ListId = listId },
        };

    [Fact]
    public async Task A_new_row_gets_at_most_one_category_tag_and_a_plain_row_gets_none()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder,
            [
                Arrival(1, "Friends <notification@facebookmail.com>"),
                Arrival(2),
                Arrival(3, "Shop <deals@shop.example.com>", subject: "Flash sale: 20% off", automated: true),
                Arrival(4, "Digest <digest@lists.example.net>", subject: "[announce] Weekly digest", automated: true, listId: "<announce.lists.example.net>"),
            ],
            ReferencesThreader.Instance,
            ct);

        Assert.Equal([Social], TagsOf(temp, folder, 1, ct));
        Assert.Empty(TagsOf(temp, folder, 2, ct));
        Assert.Equal([Marketing], TagsOf(temp, folder, 3, ct));
        Assert.Equal([News], TagsOf(temp, folder, 4, ct));
    }

    [Fact]
    public async Task Tags_are_searchable_with_a_plain_tag_operator()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder,
            [Arrival(1, "n@facebookmail.com"), Arrival(2), Arrival(3, "writer@substack.com")],
            ReferencesThreader.Instance,
            ct);

        Assert.Equal([1u], Uids(temp, "tag:auto.social", ct));
        Assert.Equal([3u], Uids(temp, "tag:auto.news", ct));
        Assert.Equal([2u, 3u], Uids(temp, "-tag:auto.social", ct));
        Assert.Equal([2u], Uids(temp, "-tag:auto.social -tag:auto.news", ct));
        Assert.Equal([1u], Uids(temp, "TAG:Auto.Social", ct));
    }

    [Fact]
    public async Task A_refreshed_row_is_never_categorised_again_after_the_user_removed_the_tag()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);
        var from = "n@facebookmail.com";

        await temp.Store.IngestEnvelopesAsync(folder, [Arrival(1, from)], ReferencesThreader.Instance, ct);
        var id = Id(temp, folder, 1, ct);
        await temp.Store.ApplyTagsAsync(id, new TagDelta { Remove = [Social] }, ct);
        Assert.Empty(temp.Store.GetTags(id, ct));

        await temp.Store.IngestEnvelopesAsync(folder, [Arrival(1, from)], ReferencesThreader.Instance, ct);
        await temp.Store.ApplySyncBatchAsync(
            new SyncBatch { FolderId = folder, Added = [Arrival(1, from, subject: "Changed subject")] },
            ReferencesThreader.Instance,
            ct);

        Assert.Empty(temp.Store.GetTags(id, ct));
    }

    [Fact]
    public async Task A_refresh_does_not_categorise_a_row_that_was_stored_without_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Arrival(1, "someone@example.com")], ReferencesThreader.Instance, ct);
        await temp.Store.IngestEnvelopesAsync(folder, [Arrival(1, "n@facebookmail.com")], ReferencesThreader.Instance, ct);

        Assert.Empty(TagsOf(temp, folder, 1, ct));
    }

    [Fact]
    public async Task A_category_the_user_added_by_hand_is_kept_and_other_tags_are_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Arrival(1)], ReferencesThreader.Instance, ct);
        var id = Id(temp, folder, 1, ct);
        await temp.Store.ApplyTagsAsync(id, new TagDelta { Add = [News, Tag.Parse("work")] }, ct);
        await temp.Store.IngestEnvelopesAsync(folder, [Arrival(1)], ReferencesThreader.Instance, ct);

        Assert.Equal([News, Tag.Parse("work")], temp.Store.GetTags(id, ct));
    }

    [Fact]
    public async Task A_reused_uid_is_a_new_message_and_swaps_the_category_but_keeps_other_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Arrival(1, "n@facebookmail.com", messageId: "first@example.com")], ReferencesThreader.Instance, ct);
        var id = Id(temp, folder, 1, ct);
        await temp.Store.ApplyTagsAsync(id, new TagDelta { Add = [Tag.Parse("work")] }, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Arrival(1, "writer@substack.com", messageId: "second@example.com")], ReferencesThreader.Instance, ct);
        Assert.Equal([News, Tag.Parse("work")], temp.Store.GetTags(id, ct));

        await temp.Store.IngestEnvelopesAsync(folder, [Arrival(1, "plain@example.com", messageId: "third@example.com")], ReferencesThreader.Instance, ct);
        Assert.Equal([Tag.Parse("work")], temp.Store.GetTags(id, ct));
    }

    [Fact]
    public async Task A_removed_category_stays_removed_when_the_same_message_is_seen_again_under_the_same_uid()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        var first = Arrival(1, "n@facebookmail.com", messageId: "same@example.com");
        await temp.Store.IngestEnvelopesAsync(folder, [first], ReferencesThreader.Instance, ct);
        var id = Id(temp, folder, 1, ct);
        await temp.Store.ApplyTagsAsync(id, new TagDelta { Remove = [Social] }, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [first], ReferencesThreader.Instance, ct);

        Assert.Empty(temp.Store.GetTags(id, ct));
    }

    [Fact]
    public async Task A_message_moved_here_keeps_its_row_and_so_its_tags_when_the_server_reports_the_new_uid()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(temp.Store, account, "Archive", FolderRole.Archive, ct);

        await temp.Store.IngestEnvelopesAsync(inbox, [Arrival(1, "n@facebookmail.com", messageId: "moved@example.com")], ReferencesThreader.Instance, ct);
        var id = Id(temp, inbox, 1, ct);
        await temp.Store.ApplyTagsAsync(id, new TagDelta { Remove = [Social] }, ct);
        await temp.Store.MoveMessageAsync(id, archive, null, ct);

        await temp.Store.IngestEnvelopesAsync(archive, [Arrival(9, "n@facebookmail.com", messageId: "moved@example.com")], ReferencesThreader.Instance, ct);

        Assert.Equal(id, Id(temp, archive, 9, ct));
        Assert.Empty(temp.Store.GetTags(id, ct));
    }

    [Fact]
    public async Task A_uidvalidity_reset_re_enumerates_the_folder_as_new_rows_that_are_categorised_afresh()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(folder, [Arrival(1, "n@facebookmail.com")], ReferencesThreader.Instance, ct);
        var before = Id(temp, folder, 1, ct);
        await temp.Store.ApplyTagsAsync(before, new TagDelta { Remove = [Social] }, ct);

        await temp.Store.InvalidateFolderUidsAsync(folder, new UidValidity(777), ct);
        Assert.Empty(temp.Store.GetTags(before, ct));

        await temp.Store.IngestEnvelopesAsync(folder, [Arrival(1, "n@facebookmail.com")], ReferencesThreader.Instance, ct);

        Assert.Equal([Social], TagsOf(temp, folder, 1, ct));
    }

    [Fact]
    public async Task Bulk_ingest_categorises_new_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await FolderAsync(temp, ct);

        await using (var bulk = await temp.Store.BeginBulkIngestAsync(ReferencesThreader.Instance, ct))
        {
            await bulk.AddAsync(folder, [Arrival(1, "n@facebookmail.com"), Arrival(2)], ct);
            await bulk.CompleteAsync(ct);
        }

        Assert.Equal([Social], TagsOf(temp, folder, 1, ct));
        Assert.Empty(TagsOf(temp, folder, 2, ct));
    }

    private static async Task<FolderId> FolderAsync(TempStore temp, CancellationToken ct)
    {
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        return await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
    }

    private static LocalMessageId Id(TempStore temp, FolderId folder, uint uid, CancellationToken ct) =>
        Require.Value(temp.Store.FindMessage(folder, new Uid(uid), ct), "the message id");

    private static IReadOnlyList<Tag> TagsOf(TempStore temp, FolderId folder, uint uid, CancellationToken ct) =>
        temp.Store.GetTags(Id(temp, folder, uid, ct), ct);

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
