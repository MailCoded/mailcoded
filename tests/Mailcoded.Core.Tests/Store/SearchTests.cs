using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class SearchTests
{
    private const string CjkSubject = "关于下季项目进度的说明";
    private const string CjkBody = "你好，下周的项目进度报告已经完成。请查收。";
    private const string JapaneseSubject = "日本語のご案内";
    private const string JapaneseBody = "見積もりの件についてご連絡いたします。";

    [Fact]
    public async Task LatinTermsGoThroughTheFtsIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var result = Run(temp.Store, "quarterly", ct);

        Assert.Equal(SearchRoute.Fts, result.Route);
        Assert.Equal(new[] { "Quarterly report ready" }, Subjects(result));
    }

    [Fact]
    public async Task AQuotedPhraseMatchesOnlyInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var inOrder = Run(temp.Store, "\"quarterly report\"", ct);
        Assert.Equal(SearchRoute.Fts, inOrder.Route);
        Assert.Equal(new[] { "Quarterly report ready" }, Subjects(inOrder));

        var reversed = Run(temp.Store, "\"report quarterly\"", ct);
        Assert.Equal(SearchRoute.Fts, reversed.Route);
        Assert.Empty(reversed.Hits);
    }

    [Fact]
    public async Task DiacriticsAreFoldedByTheFtsTokenizer()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        foreach (var query in new[] { "reunion", "reunión", "manana" })
        {
            var result = Run(temp.Store, query, ct);
            Assert.Equal(SearchRoute.Fts, result.Route);
            Assert.Equal(new[] { "Reunión de mañana" }, Subjects(result));
        }
    }

    [Fact]
    public async Task CjkOfThreeOrMoreRunesGoesThroughTheTrigramIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var fromSubject = Run(temp.Store, "项目进度", ct);
        Assert.Equal(SearchRoute.Cjk, fromSubject.Route);
        Assert.Equal(new[] { CjkSubject }, Subjects(fromSubject));

        var fromBody = Run(temp.Store, "已经完成", ct);
        Assert.Equal(SearchRoute.Cjk, fromBody.Route);
        Assert.Equal(new[] { CjkSubject }, Subjects(fromBody));
    }

    [Fact]
    public async Task ShortCjkFallsBackToLike()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var fromSubject = Run(temp.Store, "日本", ct);
        Assert.Equal(SearchRoute.Like, fromSubject.Route);
        Assert.Equal(new[] { JapaneseSubject }, Subjects(fromSubject));

        var fromBody = Run(temp.Store, "見積", ct);
        Assert.Equal(SearchRoute.Like, fromBody.Route);
        Assert.Equal(new[] { JapaneseSubject }, Subjects(fromBody));
    }

    [Fact]
    public async Task NegatedTermsAreExcluded()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var both = Run(temp.Store, "meeting", ct);
        Assert.Equal(new[] { "Budget meeting minutes", "Weekly meeting agenda" }, Subjects(both));

        var negated = Run(temp.Store, "meeting -budget", ct);
        Assert.Equal(SearchRoute.Fts, negated.Route);
        Assert.Equal(new[] { "Weekly meeting agenda" }, Subjects(negated));
    }

    [Fact]
    public async Task MultipleTagsAreAndedAndNegatedTagsExcluded()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var anded = Run(temp.Store, "tag:reports tag:urgent", ct);
        Assert.Equal(SearchRoute.None, anded.Route);
        Assert.Equal(new[] { "Quarterly report ready", "Weekly meeting agenda" }, Subjects(anded));

        var excluded = Run(temp.Store, "tag:reports -tag:urgent", ct);
        Assert.Equal(SearchRoute.None, excluded.Route);
        Assert.Equal(new[] { "Reunión de mañana" }, Subjects(excluded));
    }

    [Fact]
    public async Task DateRangesBoundTheResultSet()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var window = Run(temp.Store, "after:2026-02-01 before:2026-04-01", ct);
        Assert.Equal(SearchRoute.None, window.Route);
        Assert.Equal(new[] { "Reunión de mañana", CjkSubject }, Subjects(window));

        var before = Run(temp.Store, "before:2026-02-01", ct);
        Assert.Equal(new[] { "Quarterly report ready" }, Subjects(before));
    }

    [Fact]
    public async Task APureMetadataQueryNeedsNoTextAtAll()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var result = Run(temp.Store, "is:unread has:attachment", ct);

        Assert.Equal(SearchRoute.None, result.Route);
        Assert.Equal(new[] { "Quarterly report ready" }, Subjects(result));
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task AddressFolderAndFlagPredicatesAreServedBySql()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(new[] { "Reunión de mañana" }, Subjects(Run(temp.Store, "from:jose@example.es", ct)));
        Assert.Equal(new[] { "Weekly meeting agenda" }, Subjects(Run(temp.Store, "subject:agenda", ct)));
        Assert.Equal(new[] { "Archived note" }, Subjects(Run(temp.Store, "folder:Archive", ct)));
        Assert.Equal(new[] { "Archived note" }, Subjects(Run(temp.Store, "in:archive", ct)));
        Assert.Equal(new[] { "Budget meeting minutes" }, Subjects(Run(temp.Store, "is:flagged", ct)));
        Assert.DoesNotContain("Quarterly report ready", Subjects(Run(temp.Store, "-has:attachment", ct)));
    }

    [Fact]
    public async Task ScopingToAFolderIgnoresEverythingElse()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var seeded = await SeedAsync(temp, ct);

        var scoped = temp.Store.Search(
            new StoreSearchRequest
            {
                Query = SearchQueryParser.Parse("note").Query,
                FolderId = seeded.Inbox,
            },
            ct);

        Assert.Empty(scoped.Hits);

        var inArchive = temp.Store.Search(
            new StoreSearchRequest
            {
                Query = SearchQueryParser.Parse("note").Query,
                FolderId = seeded.Archive,
            },
            ct);

        Assert.Equal(new[] { "Archived note" }, Subjects(inArchive));
    }

    [Fact]
    public async Task ASnippetIsBuiltAroundTheMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var hit = Assert.Single(Run(temp.Store, "molestias", ct).Hits);
        Assert.Contains("molestias", Require.Ref(hit.Snippet, "the snippet"), StringComparison.Ordinal);

        var withoutSnippet = temp.Store.Search(
            new StoreSearchRequest
            {
                Query = SearchQueryParser.Parse("molestias").Query,
                IncludeSnippet = false,
            },
            ct);

        Assert.Null(Assert.Single(withoutSnippet.Hits).Snippet);
    }

    [Fact]
    public async Task RelevancePagingReportsTruncationOnceItRunsOutOfOffset()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create(configure: options => options with { MaxSearchOffset = 4 });
        await BulkSeedAsync(temp, 10, ct);

        var first = Run(temp.Store, "widget", ct, limit: 2);
        Assert.Equal(SearchRoute.Fts, first.Route);
        Assert.Equal(2, first.Hits.Count);
        Assert.False(first.Truncated);
        Assert.Equal("o2", first.NextCursor);

        var second = Run(temp.Store, "widget", ct, limit: 2, cursor: first.NextCursor);
        Assert.Equal(2, second.Hits.Count);
        Assert.Null(second.NextCursor);
        Assert.True(second.Truncated);

        var pastTheCap = Run(temp.Store, "widget", ct, limit: 2, cursor: "o4");
        Assert.Empty(pastTheCap.Hits);
        Assert.True(pastTheCap.Truncated);
        Assert.Null(pastTheCap.NextCursor);
    }

    [Fact]
    public async Task DateOrderedPagingReachesEveryMatchAndNeverClaimsTruncation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create(configure: options => options with { MaxSearchOffset = 4 });
        await BulkSeedAsync(temp, 10, ct);

        var ids = new List<long>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = Run(temp.Store, "widget", ct, order: SearchOrder.Date, limit: 3, cursor: cursor);
            pages++;

            Assert.Equal(SearchRoute.Fts, page.Route);
            Assert.False(page.Truncated);

            foreach (var hit in page.Hits) ids.Add(hit.Id.Value);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(10, ids.Count);
        Assert.Equal(10, new HashSet<long>(ids).Count);
        Assert.Equal(4, pages);

        for (var i = 1; i < ids.Count; i++) Assert.True(ids[i] < ids[i - 1]);
    }

    [Fact]
    public async Task MetadataOnlyPagingAlsoReachesAnyDepth()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create(configure: options => options with { MaxSearchOffset = 4 });
        await BulkSeedAsync(temp, 10, ct);

        var ids = new List<long>();
        string? cursor = null;

        do
        {
            var page = Run(temp.Store, "is:unread", ct, limit: 3, cursor: cursor);
            Assert.Equal(SearchRoute.None, page.Route);
            Assert.False(page.Truncated);

            foreach (var hit in page.Hits) ids.Add(hit.Id.Value);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(10, ids.Count);
        Assert.Equal(10, new HashSet<long>(ids).Count);
    }

    [Fact]
    public async Task AnEmptyQueryMatchesNothingIndexableAndStillPages()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var result = Run(temp.Store, "   ", ct, limit: 3);

        Assert.Equal(SearchRoute.None, result.Route);
        Assert.Equal(3, result.Hits.Count);
        Assert.NotNull(result.NextCursor);
        Assert.False(result.Truncated);
    }

    private static StoreSearchResult Run(
        SqliteStore store,
        string query,
        CancellationToken ct,
        SearchOrder order = SearchOrder.Relevance,
        int limit = 50,
        string? cursor = null) =>
        store.Search(
            new StoreSearchRequest
            {
                Query = SearchQueryParser.Parse(query).Query,
                Order = order,
                Limit = limit,
                Cursor = cursor,
            },
            ct);

    private static string[] Subjects(StoreSearchResult result)
    {
        var subjects = new List<string>(result.Hits.Count);
        foreach (var hit in result.Hits) subjects.Add(hit.Subject ?? string.Empty);
        subjects.Sort(StringComparer.Ordinal);
        return [.. subjects];
    }

    private static async Task<(FolderId Inbox, FolderId Archive)> SeedAsync(TempStore temp, CancellationToken ct)
    {
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(temp.Store, account, "Archive", FolderRole.Archive, ct);

        await StoreSeed.MessageAsync(
            temp.Store,
            inbox,
            StoreSeed.Envelope(
                1,
                subject: "Quarterly report ready",
                from: "alice@example.com",
                date: new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero),
                flags: MessageFlags.Unread,
                hasAttachments: true),
            ct,
            bodyText: "The quarterly report is attached. Budget numbers included.",
            tags: StoreSeed.Tags("reports", "urgent"));

        await StoreSeed.MessageAsync(
            temp.Store,
            inbox,
            StoreSeed.Envelope(
                2,
                subject: "Reunión de mañana",
                from: "jose@example.es",
                date: new DateTimeOffset(2026, 2, 10, 9, 0, 0, TimeSpan.Zero),
                flags: MessageFlags.None),
            ct,
            bodyText: "La reunión de mañana se ha pospuesto. Perdón por las molestias.",
            tags: StoreSeed.Tags("reports"));

        await StoreSeed.MessageAsync(
            temp.Store,
            inbox,
            StoreSeed.Envelope(
                3,
                subject: CjkSubject,
                from: "zhangsan@example.cn",
                date: new DateTimeOffset(2026, 3, 15, 9, 0, 0, TimeSpan.Zero),
                flags: MessageFlags.Unread),
            ct,
            bodyText: CjkBody);

        await StoreSeed.MessageAsync(
            temp.Store,
            inbox,
            StoreSeed.Envelope(
                4,
                subject: JapaneseSubject,
                from: "yamada@example.jp",
                date: new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.Zero),
                flags: MessageFlags.Unread),
            ct,
            bodyText: JapaneseBody);

        await StoreSeed.MessageAsync(
            temp.Store,
            inbox,
            StoreSeed.Envelope(
                5,
                subject: "Budget meeting minutes",
                from: "finance@example.com",
                date: new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero),
                flags: MessageFlags.Flagged),
            ct,
            bodyText: "meeting about the budget only",
            tags: StoreSeed.Tags("spam"));

        await StoreSeed.MessageAsync(
            temp.Store,
            inbox,
            StoreSeed.Envelope(
                6,
                subject: "Weekly meeting agenda",
                from: "chair@example.com",
                date: new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
                flags: MessageFlags.Unread),
            ct,
            bodyText: "meeting agenda for the week",
            tags: StoreSeed.Tags("reports", "urgent"));

        await StoreSeed.MessageAsync(
            temp.Store,
            archive,
            StoreSeed.Envelope(
                7,
                subject: "Archived note",
                from: "archivist@example.com",
                date: new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
                flags: MessageFlags.None),
            ct,
            bodyText: "an old note kept for the record");

        return (inbox, archive);
    }

    private static async Task<FolderId> BulkSeedAsync(TempStore temp, int count, CancellationToken ct)
    {
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        var envelopes = new List<RemoteEnvelope>(count);
        for (var i = 1; i <= count; i++)
        {
            envelopes.Add(StoreSeed.Envelope(
                (uint)i,
                subject: $"widget number {i}",
                date: StoreSeed.BaseDate.AddMinutes(i),
                flags: MessageFlags.Unread));
        }

        await temp.Store.IngestEnvelopesAsync(folder, envelopes, null, ct);

        for (var i = 1; i <= count; i++)
        {
            var id = Require.Value(temp.Store.FindMessage(folder, new Uid((uint)i), ct), "the seeded message");
            await temp.Store.SetBodyTextAsync(id, $"a widget body for row {i}", null, false, ct);
        }

        return folder;
    }
}
