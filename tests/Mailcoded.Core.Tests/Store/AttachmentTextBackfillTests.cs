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
    public async Task A_blob_file_that_cannot_be_opened_leaves_the_message_queued_and_does_not_hold_up_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create(configure: o => o with { InlineBlobThresholdBytes = 1 });
        var (folder, _) = await AddFetchedAsync(temp, null, 1, DocxKit.Message("Lost", DocxKit.Text("text/plain", "a.txt", "lostword")), ct);

        var held = Path.Combine(temp.Workspace.Root, "held");
        Directory.CreateDirectory(held);
        var moved = new List<(string From, string To)>();
        foreach (var file in Directory.EnumerateFiles(temp.Workspace.BlobDirectory, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(held, moved.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(file, to);
            moved.Add((file, to));
        }

        Assert.NotEmpty(moved);
        await AddFetchedAsync(temp, folder, 2, DocxKit.Message("Fine", DocxKit.Text("text/plain", "b.txt", "fineword")), ct);

        var backfill = new AttachmentTextBackfill(
            temp.Store, MessageParser.Default, new AttachmentTextBackfillOptions { RetryAfter = TimeSpan.FromHours(1) });
        var first = await backfill.RunBatchAsync(ct);

        Assert.Equal(1, first.WithText);
        Assert.Equal(1, first.Remaining);
        Assert.Equal(1L, backfill.Remaining(ct));
        Assert.Single(Find(temp.Store, "attachment:fineword", ct).Hits);
        Assert.Empty(Find(temp.Store, "attachment:lostword", ct).Hits);

        Assert.Equal(0, (await backfill.RunBatchAsync(ct)).Processed);

        foreach (var (from, to) in moved) File.Move(to, from);
        var retrying = new AttachmentTextBackfill(temp.Store, MessageParser.Default);
        Assert.Equal(1, (await retrying.RunBatchAsync(ct)).WithText);
        Assert.Single(Find(temp.Store, "attachment:lostword", ct).Hits);
        Assert.Equal(0L, retrying.Remaining(ct));
    }

    [Fact]
    public async Task Text_read_from_a_blob_the_message_no_longer_points_at_is_not_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, id) = await AddFetchedAsync(temp, null, 1, DocxKit.Message("M", DocxKit.Text("text/plain", "a.txt", "staleword")), ct);
        var work = Assert.Single(temp.Store.NextAttachmentIndexWork(AttachmentTextIndexing.ExtractorVersion, 10, ct));
        var entries = new[] { new AttachmentTextEntry { Index = 0, MimeType = "text/plain", Text = "staleword" } };

        Assert.False(await temp.Store.SetAttachmentTextsAsync(
            id, entries, AttachmentTextIndexing.ExtractorVersion, new BlobId(work.BlobId.Value + 1000), ct));
        Assert.Empty(Find(temp.Store, "attachment:staleword", ct).Hits);
        Assert.Equal(1L, temp.Store.CountPendingAttachmentIndex(AttachmentTextIndexing.ExtractorVersion, ct));

        Assert.True(await temp.Store.SetAttachmentTextsAsync(id, entries, AttachmentTextIndexing.ExtractorVersion, work.BlobId, ct));
        Assert.Single(Find(temp.Store, "attachment:staleword", ct).Hits);
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
    public async Task A_spent_budget_stops_the_batch_before_the_next_message_is_read()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        FolderId? folder = null;
        for (var uid = 1u; uid <= 3; uid++)
            folder = (await AddFetchedAsync(temp, folder, uid, DocxKit.Message("M", DocxKit.Text("text/plain", "a.txt", $"budgetword{uid}")), ct)).Folder;

        var backfill = new AttachmentTextBackfill(temp.Store, MessageParser.Default);

        var stopped = await backfill.RunBatchAsync(TimeSpan.Zero, ct);

        Assert.Equal(0, stopped.Processed);
        Assert.Equal(3, stopped.Remaining);
        Assert.Empty(Find(temp.Store, "attachment:budgetword1", ct).Hits);

        var finished = await backfill.RunBatchAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(3, finished.Processed);
        Assert.Equal(0, finished.Remaining);
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

    [Fact]
    public async Task The_loop_drains_the_queue_at_the_pacing_delay_then_idles()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        FolderId? folder = null;
        for (var uid = 1u; uid <= 3; uid++)
            folder = (await AddFetchedAsync(temp, folder, uid, DocxKit.Message("M", DocxKit.Text("text/plain", "a.txt", $"loopword{uid}")), ct)).Folder;

        var options = new AttachmentTextBackfillOptions
        {
            BatchSize = 1,
            PacingDelay = TimeSpan.FromMilliseconds(1),
            BusyDelay = TimeSpan.FromHours(1),
            IdleDelay = TimeSpan.FromHours(1),
        };
        var backfill = new AttachmentTextBackfill(temp.Store, MessageParser.Default, options);
        var reports = new RecordingProgress();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var run = backfill.RunAsync(reports, stop.Token);
        await reports.WaitForAsync(r => r.Count(x => x.Processed == 0) == 1, ct);
        await Task.Delay(200, ct);

        Assert.Equal(3, reports.Snapshot().Sum(r => r.Processed));
        Assert.Equal(1, reports.Snapshot().Count(r => r.Processed == 0));
        Assert.Single(Find(temp.Store, "attachment:loopword3", ct).Hits);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    [Fact]
    public async Task The_loop_polls_at_the_busy_delay_while_a_bulk_window_is_open_and_resumes_when_it_closes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        FolderId? folder = null;
        for (var uid = 1u; uid <= 2; uid++)
            folder = (await AddFetchedAsync(temp, folder, uid, DocxKit.Message("M", DocxKit.Text("text/plain", "a.txt", $"busyword{uid}")), ct)).Folder;

        var options = new AttachmentTextBackfillOptions
        {
            PacingDelay = TimeSpan.FromMilliseconds(1),
            BusyDelay = TimeSpan.FromMilliseconds(5),
            IdleDelay = TimeSpan.FromHours(1),
        };
        var backfill = new AttachmentTextBackfill(temp.Store, MessageParser.Default, options);
        var reports = new RecordingProgress();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task run;
        await using (await temp.Store.BeginBulkIngestAsync(null, ct))
        {
            run = backfill.RunAsync(reports, stop.Token);
            await reports.WaitForAsync(r => r.Count >= 3, ct);

            Assert.All(reports.Snapshot(), r => Assert.Equal(0, r.Processed));
            Assert.Empty(Find(temp.Store, "attachment:busyword1", ct).Hits);
        }

        await reports.WaitForAsync(r => r.Sum(x => x.Processed) == 2, ct);
        Assert.Single(Find(temp.Store, "attachment:busyword2", ct).Hits);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    private sealed class RecordingProgress : IProgress<AttachmentTextBackfillReport>
    {
        private readonly List<AttachmentTextBackfillReport> _reports = [];

        public void Report(AttachmentTextBackfillReport value)
        {
            lock (_reports) _reports.Add(value);
        }

        public IReadOnlyList<AttachmentTextBackfillReport> Snapshot()
        {
            lock (_reports) return [.. _reports];
        }

        public async Task WaitForAsync(Func<IReadOnlyList<AttachmentTextBackfillReport>, bool> condition, CancellationToken ct)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(20));

            while (!condition(Snapshot()))
                await Task.Delay(5, limit.Token);
        }
    }
}
