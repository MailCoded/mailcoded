using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

/// <summary>
/// Ranking and recall, which bare <c>ORDER BY rank</c> and an all-terms-must-match query left
/// untested: every column weighed the same, so a term in a footer counted as much as the same term
/// in the subject, and a query whose terms never co-occurred simply answered nothing.
/// </summary>
public sealed class SearchRankingTests
{
    /// <summary>
    /// The discriminating case, and the reason a naive test does not work: bm25 already favours a
    /// short field, so a one-word subject wins at equal weights by accident. Here the body says the
    /// word twenty times and the subject says it once, which equal weights rank body-first. Only a
    /// subject weight above the body weight puts the subject back on top.
    /// </summary>
    [Fact]
    public async Task ASubjectHitOutranksABodyThatRepeatsTheWord()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);

        await AddAsync(temp, folder, 1, "Notes from the meeting", string.Join(' ', Enumerable.Repeat("quarterly", 20)), ct);
        await AddAsync(temp, folder, 2, "Quarterly report", "notes about the weather and the garden", ct);

        var hits = Run(temp.Store, "quarterly", ct);

        Assert.Equal(2, hits.Hits.Count);
        Assert.Equal("Quarterly report", hits.Hits[0].Subject);
    }

    [Fact]
    public async Task RankingSurvivesWhenOnlyOneDocumentMatches()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Quarterly report", "prose", ct);

        var hits = Run(temp.Store, "quarterly", ct);

        Assert.Single(hits.Hits);
        Assert.Equal(SearchRoute.Fts, hits.Route);
    }

    /// <summary>The parser builds the wider expression; it must ask the same question, not a new one.</summary>
    [Fact]
    public void TheRelaxedExpressionIsTheSameTermsJoinedWithOr()
    {
        var parsed = SearchQueryParser.Parse("quarterly roof invoice").Query;

        // The parser quotes every term, so a term containing an FTS5 operator cannot become one.
        Assert.Equal("\"quarterly\" AND \"roof\" AND \"invoice\"", parsed.LatinMatchExpression);
        Assert.Equal("\"quarterly\" OR \"roof\" OR \"invoice\"", parsed.RelaxedLatinMatchExpression);
        Assert.True(parsed.CanRelax);
        Assert.Equal("\"quarterly\" OR \"roof\" OR \"invoice\"", parsed.Relaxed().LatinMatchExpression);
    }

    [Fact]
    public void ASingleTermHasNothingToRelax()
    {
        var parsed = SearchQueryParser.Parse("quarterly").Query;

        Assert.Null(parsed.RelaxedLatinMatchExpression);
        Assert.False(parsed.CanRelax);
    }

    [Fact]
    public void RelaxingIsIdempotentSoASecondPassCannotWidenFurther()
    {
        var once = SearchQueryParser.Parse("alpha beta").Query.Relaxed();

        Assert.False(once.CanRelax);
        Assert.Equal("\"alpha\" OR \"beta\"", once.LatinMatchExpression);
    }

    /// <summary>The feature the user feels: every term had to match, none did, and the answer was
    /// nothing. The service asks the wider question once and says that it did.</summary>
    [Fact]
    public async Task AQueryThatMatchesNothingIsRetriedWiderAndSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair quote", "the tiles above the kitchen", ct);
        await AddAsync(temp, folder, 2, "Invoice 4213", "payment terms are thirty days", ct);

        var service = Service(temp);

        var strict = service.Search(new SearchRequest { Query = "roof" }, ct);
        Assert.Single(strict.Hits);
        Assert.False(strict.Relaxed, "a query that matched on its own terms must not be widened");

        // No document holds both words, so the strict pass finds nothing.
        var widened = service.Search(new SearchRequest { Query = "roof invoice" }, ct);

        Assert.True(widened.Relaxed);
        Assert.Equal(2, widened.Hits.Count);
    }

    [Fact]
    public async Task AWiderRetryThatStillFindsNothingIsNotReportedAsRelaxed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair quote", "the tiles above the kitchen", ct);

        var result = Service(temp).Search(new SearchRequest { Query = "aardvark zeppelin" }, ct);

        Assert.Empty(result.Hits);
        Assert.False(result.Relaxed, "nothing was shown, so there is nothing to explain");
    }

    /// <summary>Page two of a relaxed search must keep asking page one's question.</summary>
    [Fact]
    public async Task PagingDoesNotWiden()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair quote", "tiles", ct);
        await AddAsync(temp, folder, 2, "Invoice 4213", "payment", ct);

        var result = Service(temp).Search(
            new SearchRequest { Query = "roof invoice", Cursor = "0" },
            ct);

        Assert.False(result.Relaxed);
        Assert.Empty(result.Hits);
    }

    private static SearchService Service(TempStore temp) =>
        new(
            temp.Store,
            new AgentPolicy(new AgentPolicyOptions(), temp.Clock),
            new AuditLog(temp.Store, temp.Clock));

    private static StoreSearchResult Run(SqliteStore store, string query, CancellationToken ct) =>
        store.Search(
            new StoreSearchRequest
            {
                Query = SearchQueryParser.Parse(query).Query,
                Order = SearchOrder.Relevance,
                Limit = 50,
            },
            ct);

    private static async Task<FolderId> SeedAsync(TempStore temp, CancellationToken ct)
    {
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        return await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
    }

    private static async Task AddAsync(
        TempStore temp,
        FolderId folder,
        uint uid,
        string subject,
        string body,
        CancellationToken ct)
    {
        await temp.Store.IngestEnvelopesAsync(
            folder,
            [StoreSeed.Envelope(uid, subject: subject, date: StoreSeed.BaseDate.AddMinutes(uid), flags: MessageFlags.Unread)],
            null,
            ct);

        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(uid), ct), "the seeded message");
        await temp.Store.SetBodyTextAsync(id, body, null, false, ct);
    }
}
