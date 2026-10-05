using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class AttachmentTextStoreTests
{
    private const int Version = 1;

    private static AttachmentTextEntry Entry(int index, string text, string? name = null, string mime = "text/plain") =>
        new() { Index = index, MimeType = mime, FileName = name ?? $"file{index}.txt", Text = text };

    private static StoreSearchResult Run(SqliteStore store, string query, CancellationToken ct, SearchOrder order = SearchOrder.Relevance) =>
        store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse(query).Query, Order = order, Limit = 50, IncludeSnippet = true }, ct);

    private static string[] Subjects(StoreSearchResult result) =>
        result.Hits.Select(h => h.Subject ?? string.Empty).Order(StringComparer.Ordinal).ToArray();

    private static long Count(SqliteStore store, string sql, CancellationToken ct) => StoreQuery.Scalar(store, sql, ct);

    private static async Task<(FolderId Inbox, LocalMessageId First, LocalMessageId Second)> SeedAsync(TempStore temp, CancellationToken ct)
    {
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        var first = await StoreSeed.MessageAsync(
            temp.Store, inbox, StoreSeed.Envelope(1, subject: "Order confirmation", hasAttachments: true), ct, bodyText: "Your order shipped today.");
        var second = await StoreSeed.MessageAsync(
            temp.Store, inbox, StoreSeed.Envelope(2, subject: "Minutes", hasAttachments: true), ct, bodyText: "Notes are attached.");

        await temp.Store.SetAttachmentTextsAsync(
            first, [Entry(0, "Purchase order 4471: copper wiring, 200 metres, invoices to follow.", "po-4471.txt")], Version, ct);
        await temp.Store.SetAttachmentTextsAsync(
            second, [Entry(1, "Action items: replace the warehouse lighting.", "actions.txt"), Entry(2, "项目进度报告已经完成", "report.txt")], Version, ct);

        return (inbox, first, second);
    }

    [Fact]
    public async Task The_attachment_operator_finds_text_that_only_an_attachment_holds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(new[] { "Order confirmation" }, Subjects(Run(temp.Store, "attachment:wiring", ct)));
        Assert.Equal(new[] { "Minutes" }, Subjects(Run(temp.Store, "attachment:lighting", ct)));
    }

    [Fact]
    public async Task Bare_words_never_reach_attachment_text()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Empty(Run(temp.Store, "wiring", ct).Hits);
        Assert.Empty(Run(temp.Store, "copper", ct).Hits);
        Assert.Empty(Run(temp.Store, "subject:wiring", ct).Hits);
        Assert.Equal(new[] { "Order confirmation" }, Subjects(Run(temp.Store, "shipped", ct)));
    }

    [Fact]
    public async Task Body_text_is_not_matched_by_the_attachment_operator()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Empty(Run(temp.Store, "attachment:shipped", ct).Hits);
    }

    [Fact]
    public async Task Stemming_phrases_and_prefixes_work_as_they_do_for_body_text()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Single(Run(temp.Store, "attachment:invoice", ct).Hits);
        Assert.Single(Run(temp.Store, "attachment:\"purchase order\"", ct).Hits);
        Assert.Empty(Run(temp.Store, "attachment:\"order purchase\"", ct).Hits);
        Assert.Single(Run(temp.Store, "attachment:coppe*", ct).Hits);
        Assert.Single(Run(temp.Store, "attachment:WIRING", ct).Hits);
    }

    [Fact]
    public async Task A_hit_names_the_attachment_and_excerpts_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var hit = Assert.Single(Run(temp.Store, "attachment:lighting", ct).Hits);

        var match = Require.Ref(hit.AttachmentMatch, "the matched attachment");
        Assert.Equal(1, match.Index);
        Assert.Equal("actions.txt", match.FileName);
        Assert.Equal("text/plain", match.MimeType);
        Assert.Contains("lighting", match.Snippet, StringComparison.Ordinal);
        Assert.Equal("Notes are attached.", hit.Snippet);
    }

    [Fact]
    public async Task Turning_snippets_off_still_names_the_attachment_but_carries_no_excerpt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var result = temp.Store.Search(
            new StoreSearchRequest { Query = SearchQueryParser.Parse("attachment:lighting").Query, IncludeSnippet = false }, ct);

        var hit = Assert.Single(result.Hits);
        Assert.Null(hit.Snippet);
        Assert.Equal(1, hit.AttachmentMatch?.Index);
        Assert.Equal("actions.txt", hit.AttachmentMatch?.FileName);
        Assert.Null(hit.AttachmentMatch?.Snippet);
    }

    [Fact]
    public async Task A_match_far_into_a_long_attachment_is_still_excerpted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, first, _) = await SeedAsync(temp, ct);

        var text = string.Concat(Enumerable.Repeat("filler words go here ", 12_000)) + "the zebra crossing is here";
        await temp.Store.SetAttachmentTextsAsync(first, [Entry(0, text, "long.txt")], Version, ct);

        var hit = Assert.Single(Run(temp.Store, "attachment:zebra", ct).Hits);

        Assert.Contains("zebra", hit.AttachmentMatch?.Snippet, StringComparison.Ordinal);
        Assert.True(hit.AttachmentMatch?.Snippet?.Length < 400);
    }

    [Fact]
    public async Task Only_the_attachment_that_matches_is_named()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var hit = Assert.Single(Run(temp.Store, "attachment:warehouse", ct).Hits);

        Assert.Equal(1, hit.AttachmentMatch?.Index);
    }

    [Fact]
    public async Task A_query_without_the_operator_carries_no_attachment_match()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.All(Run(temp.Store, "order", ct).Hits, hit => Assert.Null(hit.AttachmentMatch));
        Assert.All(Run(temp.Store, "-attachment:wiring", ct).Hits, hit => Assert.Null(hit.AttachmentMatch));
    }

    [Fact]
    public async Task A_negated_operator_keeps_everything_that_lacks_the_text()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(new[] { "Minutes" }, Subjects(Run(temp.Store, "-attachment:wiring", ct, SearchOrder.Date)));
    }

    [Fact]
    public async Task The_operator_combines_with_body_words_and_other_filters()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var both = Run(temp.Store, "shipped attachment:wiring", ct);
        Assert.Equal(SearchRoute.Fts, both.Route);
        Assert.Equal(new[] { "Order confirmation" }, Subjects(both));

        Assert.Empty(Run(temp.Store, "shipped attachment:lighting", ct).Hits);
        Assert.Equal(new[] { "Order confirmation" }, Subjects(Run(temp.Store, "attachment:wiring has:attachment is:unread", ct)));
        Assert.Empty(Run(temp.Store, "attachment:wiring is:read", ct).Hits);
    }

    [Fact]
    public async Task Two_operators_must_each_be_satisfied()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(new[] { "Minutes" }, Subjects(Run(temp.Store, "attachment:warehouse attachment:lighting", ct)));
        Assert.Empty(Run(temp.Store, "attachment:warehouse attachment:wiring", ct).Hits);
    }

    [Fact]
    public async Task The_operator_alone_pages_by_date()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(SearchRoute.None, Run(temp.Store, "attachment:wiring", ct).Route);
    }

    [Fact]
    public async Task Cjk_attachment_text_is_found_by_trigram_and_by_the_short_fallback()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(new[] { "Minutes" }, Subjects(Run(temp.Store, "attachment:项目进度", ct)));
        Assert.Equal(new[] { "Minutes" }, Subjects(Run(temp.Store, "attachment:进度", ct)));
        var hit = Assert.Single(Run(temp.Store, "attachment:项目进度", ct).Hits);
        Assert.Equal(2, hit.AttachmentMatch?.Index);
        Assert.Contains("项目进度", hit.AttachmentMatch?.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_text_with_cjk_characters_enters_the_trigram_index()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(3L, Count(temp.Store, "SELECT COUNT(*) FROM attachment_text", ct));
        Assert.Equal(3L, Count(temp.Store, "SELECT COUNT(*) FROM att_fts", ct));
        Assert.Equal(1L, Count(temp.Store, "SELECT COUNT(*) FROM att_fts_cjk", ct));
    }

    [Fact]
    public async Task Replacing_the_text_leaves_no_stale_index_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, first, _) = await SeedAsync(temp, ct);

        await temp.Store.SetAttachmentTextsAsync(first, [Entry(0, "Revised: aluminium conduit only.")], Version, ct);

        Assert.Empty(Run(temp.Store, "attachment:wiring", ct).Hits);
        Assert.Single(Run(temp.Store, "attachment:conduit", ct).Hits);
        Assert.Equal(0L, Count(temp.Store, "SELECT COUNT(*) FROM att_fts WHERE att_fts MATCH 'wiring'", ct));
        Assert.Equal(3L, Count(temp.Store, "SELECT COUNT(*) FROM attachment_text", ct));
    }

    [Fact]
    public async Task An_empty_list_clears_the_text_and_still_marks_the_message_read()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, first, _) = await SeedAsync(temp, ct);

        Assert.True(await temp.Store.SetAttachmentTextsAsync(first, [], Version, ct));

        Assert.Empty(temp.Store.GetAttachmentTexts(first, ct));
        Assert.Empty(Run(temp.Store, "attachment:wiring", ct).Hits);
        Assert.Equal(1L, Count(temp.Store, $"SELECT COUNT(*) FROM attachment_index WHERE message_id = {first.Value} AND version = {Version}", ct));
    }

    [Fact]
    public async Task A_message_that_does_not_exist_is_reported_not_thrown()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.False(await temp.Store.SetAttachmentTextsAsync(new LocalMessageId(99_999), [Entry(0, "x")], Version, ct));
        Assert.Equal(3L, Count(temp.Store, "SELECT COUNT(*) FROM attachment_text", ct));
    }

    [Fact]
    public async Task Expunging_a_message_takes_its_attachment_text_and_index_rows_with_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (inbox, _, _) = await SeedAsync(temp, ct);

        await temp.Store.ExpungeAsync(inbox, [new Uid(1)], ct);

        Assert.Empty(Run(temp.Store, "attachment:wiring", ct).Hits);
        Assert.Equal(2L, Count(temp.Store, "SELECT COUNT(*) FROM attachment_text", ct));
        Assert.Equal(2L, Count(temp.Store, "SELECT COUNT(*) FROM att_fts", ct));
        Assert.Equal(1L, Count(temp.Store, "SELECT COUNT(*) FROM attachment_index", ct));
    }

    [Fact]
    public async Task Removing_a_Folder_takes_the_attachment_text_of_its_messages()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (inbox, _, _) = await SeedAsync(temp, ct);

        await temp.Store.RemoveFolderAsync(inbox, ct);

        Assert.Equal(0L, Count(temp.Store, "SELECT COUNT(*) FROM attachment_text", ct));
        Assert.Equal(0L, Count(temp.Store, "SELECT COUNT(*) FROM att_fts", ct));
        Assert.Equal(0L, Count(temp.Store, "SELECT COUNT(*) FROM att_fts_cjk", ct));
        Assert.Equal(0L, Count(temp.Store, "SELECT COUNT(*) FROM attachment_index", ct));
    }

    [Fact]
    public async Task Forgetting_an_Account_takes_its_attachment_text_and_leaves_the_others()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var other = await StoreSeed.AccountAsync(temp.Store, ct, "other@example.org");
        var folder = await StoreSeed.FolderAsync(temp.Store, other, "INBOX", FolderRole.Inbox, ct);
        var kept = await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(1, subject: "Kept", hasAttachments: true), ct, bodyText: "kept body");
        await temp.Store.SetAttachmentTextsAsync(kept, [Entry(0, "survivor text about copper")], Version, ct);

        var first = temp.Store.ListAccounts(ct).First(a => a.Email == "tester@example.com");
        await temp.Store.ForgetAccountAsync(first.Id, ct);

        Assert.Equal(1L, Count(temp.Store, "SELECT COUNT(*) FROM attachment_text", ct));
        Assert.Equal(1L, Count(temp.Store, "SELECT COUNT(*) FROM att_fts", ct));
        Assert.Equal(new[] { "Kept" }, Subjects(Run(temp.Store, "attachment:copper", ct)));
    }

    [Fact]
    public async Task A_reused_UID_forgets_what_the_previous_occupant_attached()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (inbox, first, _) = await SeedAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            inbox, [StoreSeed.Envelope(1, subject: "A different message", messageId: "other-1@example.com", hasAttachments: true)], null, ct);

        Assert.Empty(temp.Store.GetAttachmentTexts(first, ct));
        Assert.Empty(Run(temp.Store, "attachment:wiring", ct).Hits);
        Assert.Equal(0L, Count(temp.Store, $"SELECT COUNT(*) FROM attachment_index WHERE message_id = {first.Value}", ct));
    }

    [Fact]
    public async Task Search_hides_Trash_and_Junk_attachment_hits_like_any_other()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var trash = await StoreSeed.FolderAsync(temp.Store, account, "Trash", FolderRole.Trash, ct);
        var id = await StoreSeed.MessageAsync(temp.Store, trash, StoreSeed.Envelope(1, subject: "Binned", hasAttachments: true), ct, bodyText: "b");
        await temp.Store.SetAttachmentTextsAsync(id, [Entry(0, "discarded wiring diagram")], Version, ct);

        Assert.Empty(Run(temp.Store, "attachment:wiring", ct).Hits);
        Assert.Single(Run(temp.Store, "attachment:wiring in:trash", ct).Hits);
    }

    [Theory]
    [InlineData("attachment:\"unterminated")]
    [InlineData("attachment:NEAR(a b)")]
    [InlineData("attachment:a*b*")]
    [InlineData("attachment:\"a\"\" OR \"b\"")]
    [InlineData("attachment:'; DROP TABLE attachment_text; --")]
    [InlineData("attachment:* ")]
    [InlineData("attachment:")]
    [InlineData("-attachment:-")]
    [InlineData("attachment:AND")]
    [InlineData("attachment:col:val")]
    [InlineData("attachment:{text}:wiring")]
    public async Task Hostile_operator_values_never_reach_the_engine_as_syntax(string query)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var result = Run(temp.Store, query, ct);

        Assert.NotNull(result);
        Assert.Equal(3L, Count(temp.Store, "SELECT COUNT(*) FROM attachment_text", ct));
    }

    [Fact]
    public async Task The_queue_holds_messages_with_a_fetched_body_attachments_and_a_blob_and_no_current_row()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        var blob = await temp.Store.StoreBlobAsync([1, 2, 3], ct);

        var ready = await StoreSeed.MessageAsync(temp.Store, inbox, StoreSeed.Envelope(1, hasAttachments: true), ct);
        await temp.Store.SetBodyTextAsync(ready, "body", blob.Id, true, ct);

        var noAttachments = await StoreSeed.MessageAsync(temp.Store, inbox, StoreSeed.Envelope(2), ct);
        await temp.Store.SetBodyTextAsync(noAttachments, "body", blob.Id, false, ct);

        await StoreSeed.MessageAsync(temp.Store, inbox, StoreSeed.Envelope(3, hasAttachments: true), ct);

        var noBlob = await StoreSeed.MessageAsync(temp.Store, inbox, StoreSeed.Envelope(4, hasAttachments: true), ct);
        await temp.Store.SetBodyTextAsync(noBlob, "body", null, true, ct);

        var work = temp.Store.NextAttachmentIndexWork(Version, 10, ct);

        Assert.Equal(ready, Assert.Single(work).MessageId);
        Assert.Equal(blob.Id, work[0].BlobId);
        Assert.Equal(1L, temp.Store.CountPendingAttachmentIndex(Version, ct));

        await temp.Store.SetAttachmentTextsAsync(ready, [], Version, ct);

        Assert.Empty(temp.Store.NextAttachmentIndexWork(Version, 10, ct));
        Assert.Single(temp.Store.NextAttachmentIndexWork(Version + 1, 10, ct));
        Assert.Equal(0L, temp.Store.CountPendingAttachmentIndex(Version, ct));
    }

    [Fact]
    public async Task The_queue_is_served_oldest_first_and_in_batches()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var blob = await temp.Store.StoreBlobAsync([9], ct);

        var ids = new List<LocalMessageId>();
        for (var uid = 1u; uid <= 5; uid++)
        {
            var id = await StoreSeed.MessageAsync(temp.Store, inbox, StoreSeed.Envelope(uid, hasAttachments: true), ct);
            await temp.Store.SetBodyTextAsync(id, "b", blob.Id, true, ct);
            ids.Add(id);
        }

        var batch = temp.Store.NextAttachmentIndexWork(Version, 2, ct);

        Assert.Equal(ids.Take(2), batch.Select(w => w.MessageId));
        Assert.Equal(5L, temp.Store.CountPendingAttachmentIndex(Version, ct));
    }

    [Fact]
    public async Task Stats_count_the_attachment_text_table()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(3L, temp.Store.GetStats(ct).TableCounts["attachment_text"]);
    }

    [Fact]
    public async Task Optimizing_the_indexes_keeps_attachment_search_working()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        await temp.Store.OptimizeFtsAsync(ct);

        Assert.Single(Run(temp.Store, "attachment:wiring", ct).Hits);
        Assert.Single(Run(temp.Store, "attachment:项目进度", ct).Hits);
    }
}
