using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class AttachmentTextBackfillTests
{
    private static async Task<(FolderId Folder, LocalMessageId Id)> AddFetchedAsync(TempStore temp, FolderId? folder, uint uid, byte[] raw, CancellationToken ct)
    {
        var inbox = folder;
        if (inbox is null)
        {
            var account = await StoreSeed.AccountAsync(temp.Store, ct);
            inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        }

        var blob = await temp.Store.StoreBlobAsync(raw, ct);
        var id = await StoreSeed.MessageAsync(temp.Store, inbox.Value, StoreSeed.Envelope(uid, subject: $"Message {uid}", hasAttachments: true), ct);
        await temp.Store.SetBodyTextAsync(id, "See the attachment.", blob.Id, true, ct);
        return (inbox.Value, id);
    }

    private static StoreSearchResult Find(SqliteStore store, string query, CancellationToken ct) =>
        store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse(query).Query, Limit = 20 }, ct);

    [Fact]
    public async Task Already_fetched_messages_gain_searchable_attachment_text()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (folder, _) = await AddFetchedAsync(
            temp, null, 1, DocxKit.Message("Minutes", DocxKit.DocxPart(DocxKit.Paragraphs("Replace the warehouse lighting"))), ct);
        await AddFetchedAsync(temp, folder, 2, DocxKit.Message("Numbers", DocxKit.Text("text/csv", "q3.csv", "region,total\nnorth,1200")), ct);

        Assert.Empty(Find(temp.Store, "attachment:lighting", ct).Hits);

        var backfill = new AttachmentTextBackfill(temp.Store, MessageParser.Default);
        var report = await backfill.RunBatchAsync(ct);

        Assert.Equal(2, report.Processed);
        Assert.Equal(2, report.WithText);
        Assert.Equal(0, report.Remaining);
        Assert.Single(Find(temp.Store, "attachment:lighting", ct).Hits);
        Assert.Single(Find(temp.Store, "attachment:north", ct).Hits);
    }

    [Fact]
    public async Task A_message_with_nothing_readable_is_marked_done_and_never_requeued()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await AddFetchedAsync(temp, null, 1, DocxKit.Message("Scan", DocxKit.Attach("application/pdf", "a.pdf", [1, 2, 3])), ct);

        var backfill = new AttachmentTextBackfill(temp.Store, MessageParser.Default);

        Assert.Equal(1, (await backfill.RunBatchAsync(ct)).Processed);
        Assert.Equal(0, (await backfill.RunBatchAsync(ct)).Processed);
        Assert.Equal(0L, backfill.Remaining(ct));
    }

    [Fact]
    public async Task Unreadable_raw_bytes_do_not_stall_the_queue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (folder, _) = await AddFetchedAsync(temp, null, 1, [0xFF, 0xFE, 0x00, 0x01, 0x02], ct);
        await AddFetchedAsync(temp, folder, 2, DocxKit.Message("Good", DocxKit.Text("text/plain", "a.txt", "findable words")), ct);

        var report = await new AttachmentTextBackfill(temp.Store, MessageParser.Default).RunBatchAsync(ct);

        Assert.Equal(2, report.Processed);
        Assert.Equal(0, report.Remaining);
        Assert.Single(Find(temp.Store, "attachment:findable", ct).Hits);
    }

    [Fact]
    public async Task A_raw_message_over_the_size_cap_is_marked_done_unread()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await AddFetchedAsync(temp, null, 1, DocxKit.Message("Big", DocxKit.Text("text/plain", "a.txt", "never read")), ct);

        var backfill = new AttachmentTextBackfill(temp.Store, MessageParser.Default, new AttachmentTextBackfillOptions { MaxRawBytes = 10 });
        var report = await backfill.RunBatchAsync(ct);

        Assert.Equal(1, report.Processed);
        Assert.Equal(0, report.WithText);
        Assert.Empty(Find(temp.Store, "attachment:never", ct).Hits);
    }

    [Fact]
    public async Task Batches_resume_where_the_last_one_stopped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        FolderId? folder = null;
        for (var uid = 1u; uid <= 5; uid++)
            folder = (await AddFetchedAsync(temp, folder, uid, DocxKit.Message("M", DocxKit.Text("text/plain", "a.txt", $"batchword{uid}")), ct)).Folder;

        var backfill = new AttachmentTextBackfill(temp.Store, MessageParser.Default, new AttachmentTextBackfillOptions { BatchSize = 2 });

        var first = await backfill.RunBatchAsync(ct);
        Assert.Equal(2, first.Processed);
        Assert.Equal(3, first.Remaining);

        var left = await backfill.RunWithinAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(0, left);
        Assert.Single(Find(temp.Store, "attachment:batchword5", ct).Hits);
    }

    [Fact]
    public async Task A_message_indexed_at_fetch_time_is_not_read_again()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, id) = await AddFetchedAsync(temp, null, 1, DocxKit.Message("M", DocxKit.Text("text/plain", "a.txt", "fetchtime")), ct);
        await temp.Store.SetAttachmentTextsAsync(id, [new AttachmentTextEntry { Index = 0, MimeType = "text/plain", Text = "fetchtime" }], AttachmentTextIndexing.ExtractorVersion, ct);

        var report = await new AttachmentTextBackfill(temp.Store, MessageParser.Default).RunBatchAsync(ct);

        Assert.Equal(0, report.Processed);
    }

    [Fact]
    public async Task A_new_extractor_version_requeues_everything()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, id) = await AddFetchedAsync(temp, null, 1, DocxKit.Message("M", DocxKit.Text("text/plain", "a.txt", "versioned")), ct);
        await temp.Store.SetAttachmentTextsAsync(id, [], AttachmentTextIndexing.ExtractorVersion - 1, ct);

        var report = await new AttachmentTextBackfill(temp.Store, MessageParser.Default).RunBatchAsync(ct);

        Assert.Equal(1, report.Processed);
        Assert.Single(Find(temp.Store, "attachment:versioned", ct).Hits);
    }

    [Fact]
    public async Task The_backfill_stands_aside_while_a_bulk_ingest_window_is_open()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await AddFetchedAsync(temp, null, 1, DocxKit.Message("M", DocxKit.Text("text/plain", "a.txt", "deferred")), ct);
        var backfill = new AttachmentTextBackfill(temp.Store, MessageParser.Default);

        await using (await temp.Store.BeginBulkIngestAsync(null, ct))
        {
            var during = await backfill.RunBatchAsync(ct);
            Assert.Equal(0, during.Processed);
            Assert.Equal(1, during.Remaining);
        }

        Assert.Equal(1, (await backfill.RunBatchAsync(ct)).Processed);
    }

    [Fact]
    public async Task Cancellation_stops_the_loop_without_throwing()
    {
        using var temp = TempStore.Create();
        using var source = new CancellationTokenSource();
        var backfill = new AttachmentTextBackfill(temp.Store, MessageParser.Default, new AttachmentTextBackfillOptions { IdleDelay = TimeSpan.FromMinutes(5) });

        var run = backfill.RunAsync(null, source.Token);
        await source.CancelAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
