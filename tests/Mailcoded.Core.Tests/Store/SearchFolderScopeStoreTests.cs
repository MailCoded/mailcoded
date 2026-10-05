using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class SearchFolderScopeStoreTests
{
    private const string CjkSubject = "关于下季项目进度的说明";

    [Theory]
    [InlineData("invoice", SearchRoute.Fts)]
    [InlineData("项目进度", SearchRoute.Cjk)]
    [InlineData("进度", SearchRoute.Like)]
    [InlineData("", SearchRoute.None)]
    [InlineData("is:unread", SearchRoute.None)]
    public async Task Every_route_leaves_Trash_and_Junk_out_by_default(string query, SearchRoute route)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var result = Run(temp.Store, query, ct, order: SearchOrder.Date);

        Assert.Equal(route, result.Route);
        Assert.NotEmpty(result.Hits);
        Assert.DoesNotContain(result.Hits, hit => IsHiddenSubject(hit.Subject));
    }

    [Fact]
    public async Task The_default_returns_everything_else()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(
            new[] { "Invoice archive", "Invoice inbox", "Invoice other account inbox", "Invoice plain folder" },
            Subjects(Run(temp.Store, "invoice", ct)));
    }

    [Fact]
    public async Task Hiding_is_by_Folder_role_not_by_Folder_name()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Contains("Invoice plain folder", Subjects(Run(temp.Store, "invoice", ct)));
        Assert.DoesNotContain("Invoice deleted items", Subjects(Run(temp.Store, "invoice", ct)));
        Assert.DoesNotContain("Invoice spam", Subjects(Run(temp.Store, "invoice", ct)));
    }

    [Theory]
    [InlineData("invoice in:trash", new[] { "Invoice deleted items", "Invoice trashed" })]
    [InlineData("invoice folder:Trash", new[] { "Invoice deleted items", "Invoice trashed" })]
    [InlineData("invoice in:TRASH", new[] { "Invoice deleted items", "Invoice trashed" })]
    [InlineData("invoice in:junk", new[] { "Invoice spam" })]
    [InlineData("invoice in:Spam", new[] { "Invoice spam" })]
    [InlineData("invoice in:\"[Gmail]/Spam\"", new[] { "Invoice spam" })]
    [InlineData("invoice in:\"Deleted Items\"", new[] { "Invoice deleted items" })]
    [InlineData("invoice in:inbox", new[] { "Invoice inbox", "Invoice other account inbox" })]
    public async Task A_query_that_names_a_Folder_searches_it_whatever_its_role(string query, string[] expected)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(expected, Subjects(Run(temp.Store, query, ct)));
    }

    [Fact]
    public async Task Naming_a_Folder_applies_to_the_date_ordered_route_too()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(
            new[] { "Invoice deleted items", "Invoice trashed" },
            Subjects(Run(temp.Store, "invoice in:trash", ct, order: SearchOrder.Date)));
        Assert.Equal(new[] { "Invoice spam" }, Subjects(Run(temp.Store, "in:junk invoice", ct, order: SearchOrder.Date)));
    }

    [Theory]
    [InlineData("invoice in:anywhere", SearchRoute.Fts)]
    [InlineData("项目进度 in:anywhere", SearchRoute.Cjk)]
    [InlineData("进度 in:anywhere", SearchRoute.Like)]
    [InlineData("in:anywhere", SearchRoute.None)]
    [InlineData("folder:anywhere", SearchRoute.None)]
    public async Task In_anywhere_searches_every_Folder_on_every_route(string query, SearchRoute route)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var result = Run(temp.Store, query, ct, order: SearchOrder.Date);

        Assert.Equal(route, result.Route);
        Assert.Contains(result.Hits, hit => IsHiddenSubject(hit.Subject));
    }

    [Fact]
    public async Task In_anywhere_returns_all_of_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(
            new[]
            {
                "Invoice archive",
                "Invoice deleted items",
                "Invoice inbox",
                "Invoice other account inbox",
                "Invoice plain folder",
                "Invoice spam",
                "Invoice trashed",
            },
            Subjects(Run(temp.Store, "invoice in:anywhere", ct)));
    }

    [Theory]
    [InlineData("invoice -in:trash")]
    [InlineData("invoice -in:junk")]
    [InlineData("invoice -in:inbox")]
    [InlineData("invoice -folder:archive")]
    public async Task Ruling_a_Folder_out_does_not_lift_the_default(string query)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var subjects = Subjects(Run(temp.Store, query, ct));

        Assert.NotEmpty(subjects);
        Assert.DoesNotContain(subjects, IsHiddenSubject);
    }

    [Fact]
    public async Task A_negated_Folder_still_filters_what_it_names()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var subjects = Subjects(Run(temp.Store, "invoice -in:inbox", ct));

        Assert.DoesNotContain("Invoice inbox", subjects);
        Assert.Contains("Invoice archive", subjects);
    }

    [Fact]
    public async Task A_Folder_negated_and_a_Folder_named_together_search_the_named_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Equal(
            new[] { "Invoice deleted items", "Invoice trashed" },
            Subjects(Run(temp.Store, "invoice in:trash -in:inbox", ct)));
    }

    [Fact]
    public async Task A_request_scoped_to_a_Folder_searches_it_even_when_it_is_Trash()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var seeded = await SeedAsync(temp, ct);

        var inTrash = temp.Store.Search(
            new StoreSearchRequest { Query = SearchQueryParser.Parse("invoice").Query, FolderId = seeded.Trash }, ct);
        var inJunk = temp.Store.Search(
            new StoreSearchRequest { Query = SearchQueryParser.Parse("").Query, FolderId = seeded.Junk }, ct);

        Assert.Equal(new[] { "Invoice trashed" }, Subjects(inTrash));
        Assert.Equal(new[] { "Invoice spam", "进度 spam" }, Subjects(inJunk));
    }

    [Fact]
    public async Task An_Account_scope_alone_still_hides()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var seeded = await SeedAsync(temp, ct);

        var result = temp.Store.Search(
            new StoreSearchRequest { Query = SearchQueryParser.Parse("invoice").Query, AccountId = seeded.Second }, ct);

        Assert.Equal(new[] { "Invoice other account inbox" }, Subjects(result));
    }

    [Fact]
    public async Task Trash_and_Junk_of_every_Account_are_hidden()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var subjects = Subjects(Run(temp.Store, "invoice", ct));

        Assert.DoesNotContain("Invoice deleted items", subjects);
        Assert.DoesNotContain("Invoice trashed", subjects);
    }

    [Fact]
    public async Task A_Folder_that_stops_being_Trash_stops_being_hidden()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var seeded = await SeedAsync(temp, ct);

        await temp.Store.UpsertFoldersAsync(
            seeded.First,
            [new RemoteFolder { Path = FolderPath.Create("Trash"), Role = FolderRole.Archive }],
            ct);

        Assert.Contains("Invoice trashed", Subjects(Run(temp.Store, "invoice", ct)));
    }

    [Fact]
    public async Task Keyset_paging_skips_hidden_Messages_without_short_or_repeated_pages()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var trash = await StoreSeed.FolderAsync(temp.Store, account, "Trash", FolderRole.Trash, ct);

        for (var uid = 1u; uid <= 12; uid++)
        {
            await StoreSeed.MessageAsync(
                temp.Store,
                uid % 3 == 0 ? inbox : trash,
                StoreSeed.Envelope(uid, subject: $"Paged {uid}", date: StoreSeed.BaseDate.AddHours(uid)),
                ct);
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = Run(temp.Store, "", ct, order: SearchOrder.Date, limit: 2, cursor: cursor);
            pages++;
            if (page.NextCursor is not null) Assert.Equal(2, page.Hits.Count);
            seen.AddRange(page.Hits.Select(h => h.Subject ?? string.Empty));
            cursor = page.NextCursor;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(new[] { "Paged 12", "Paged 9", "Paged 6", "Paged 3" }, seen);
        Assert.Equal(2, pages);
    }

    [Fact]
    public async Task Relevance_paging_ignores_hidden_Messages_too()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var junk = await StoreSeed.FolderAsync(temp.Store, account, "Spam", FolderRole.Junk, ct);

        for (var uid = 1u; uid <= 9; uid++)
        {
            await StoreSeed.MessageAsync(
                temp.Store,
                uid % 2 == 0 ? junk : inbox,
                StoreSeed.Envelope(uid, subject: $"Ranked {uid}"),
                ct);
        }

        var first = Run(temp.Store, "ranked", ct, limit: 3);
        var second = Run(temp.Store, "ranked", ct, limit: 3, cursor: first.NextCursor);

        Assert.Equal(3, first.Hits.Count);
        Assert.Equal(2, second.Hits.Count);
        Assert.Null(second.NextCursor);
        Assert.Equal(5, first.Hits.Concat(second.Hits).Select(h => h.Id.Value).Distinct().Count());
    }

    [Fact]
    public async Task A_query_that_only_Trash_can_answer_answers_nothing_unless_it_names_a_Folder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        Assert.Empty(Run(temp.Store, "zebra", ct).Hits);
        Assert.Single(Run(temp.Store, "zebra in:trash", ct).Hits);
        Assert.Single(Run(temp.Store, "zebra in:anywhere", ct).Hits);
    }

    [Fact]
    public async Task The_relaxed_retry_hides_Trash_and_Junk_as_well()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);
        var service = Service(temp);

        var results = service.Search(new SearchRequest { Query = "zebra bogus", Semantic = false }, ct);

        Assert.True(results.Relaxed);
        Assert.Equal(new[] { "Bogus inbox" }, results.Hits.Select(h => h.Subject).ToArray());
    }

    [Fact]
    public async Task The_relaxed_retry_reaches_Trash_when_the_query_names_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);
        var service = Service(temp);

        var results = service.Search(new SearchRequest { Query = "zebra nonexistentword in:trash", Semantic = false }, ct);

        Assert.True(results.Relaxed);
        Assert.Equal(new[] { "Zebra crossing trashed" }, results.Hits.Select(h => h.Subject).ToArray());
    }

    [Fact]
    public async Task A_strict_match_inside_a_named_Folder_is_not_relaxed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var results = Service(temp).Search(new SearchRequest { Query = "zebra bogus in:anywhere", Semantic = false }, ct);

        Assert.False(results.Relaxed);
        Assert.Equal(new[] { "Zebra crossing trashed" }, results.Hits.Select(h => h.Subject).ToArray());
    }

    [Fact]
    public async Task The_service_scopes_to_a_Folder_the_same_way_the_store_does()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var seeded = await SeedAsync(temp, ct);
        var service = Service(temp);

        var results = service.Search(new SearchRequest { Query = "invoice", FolderId = seeded.Trash, Semantic = false }, ct);

        Assert.Equal(new[] { "Invoice trashed" }, results.Hits.Select(h => h.Subject).ToArray());
    }

    [Fact]
    public async Task The_default_filter_keeps_the_recency_index_and_needs_no_sort()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);

        var compiled = SqliteStore.CompiledSearchSql(new StoreSearchRequest
        {
            Query = SearchQueryParser.Parse("").Query,
            Order = SearchOrder.Date,
            Cursor = Cursors.EncodeKeyset(1000, 5),
        });
        Assert.Contains("NOT IN (SELECT id FROM folders WHERE role IN ('trash', 'junk'))", compiled, StringComparison.Ordinal);

        var rows = temp.Store.ExecuteReadOnlyQuery(
            "EXPLAIN QUERY PLAN " + compiled,
            100,
            ct).Rows;

        var plan = string.Join("\n", rows.Select(row => Convert.ToString(row[row.Count - 1], System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Contains("ix_msg_date", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);
    }

    private static SearchService Service(TempStore temp) =>
        new(temp.Store, new AgentPolicy(new AgentPolicyOptions(), temp.Clock), new AuditLog(temp.Store, temp.Clock));

    private static bool IsHiddenSubject(string? subject) =>
        subject is not null
        && (subject.Contains("trashed", StringComparison.Ordinal)
            || subject.Contains("spam", StringComparison.Ordinal)
            || subject.Contains("deleted items", StringComparison.Ordinal));

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

    private sealed record Seeded(AccountId First, AccountId Second, FolderId Trash, FolderId Junk);

    private static async Task<Seeded> SeedAsync(TempStore temp, CancellationToken ct)
    {
        var store = temp.Store;
        var first = await StoreSeed.AccountAsync(store, ct);
        var second = await StoreSeed.AccountAsync(store, ct, "other@example.net");

        var inbox = await StoreSeed.FolderAsync(store, first, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(store, first, "Archive", FolderRole.Archive, ct);
        var trash = await StoreSeed.FolderAsync(store, first, "Trash", FolderRole.Trash, ct);
        var junk = await StoreSeed.FolderAsync(store, first, "[Gmail]/Spam", FolderRole.Junk, ct);
        var plain = await StoreSeed.FolderAsync(store, first, "Trash old", FolderRole.None, ct);
        var otherInbox = await StoreSeed.FolderAsync(store, second, "INBOX", FolderRole.Inbox, ct);
        var otherTrash = await StoreSeed.FolderAsync(store, second, "Deleted Items", FolderRole.Trash, ct);

        var uid = 0u;

        async Task Add(FolderId folder, string subject, string body)
        {
            uid++;
            await StoreSeed.MessageAsync(
                store,
                folder,
                StoreSeed.Envelope(uid, subject: subject, date: StoreSeed.BaseDate.AddHours(uid)),
                ct,
                bodyText: body);
        }

        await Add(inbox, "Invoice inbox", "the invoice is attached");
        await Add(archive, "Invoice archive", "the invoice is attached");
        await Add(trash, "Invoice trashed", "the invoice is attached");
        await Add(junk, "Invoice spam", "the invoice is attached");
        await Add(plain, "Invoice plain folder", "the invoice is attached");
        await Add(otherInbox, "Invoice other account inbox", "the invoice is attached");
        await Add(otherTrash, "Invoice deleted items", "the invoice is attached");
        await Add(trash, "Zebra crossing trashed", "zebra bogus");
        await Add(inbox, "Bogus inbox", "bogus only");
        await Add(inbox, CjkSubject, "你好");
        await Add(trash, "项目进度 trashed", "项目进度报告");
        await Add(junk, "进度 spam", "进度");

        return new Seeded(first, second, trash, junk);
    }
}
