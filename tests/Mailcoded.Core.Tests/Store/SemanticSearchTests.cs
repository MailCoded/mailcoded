using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Embedding;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

/// <summary>The tiny model's weights are random, so it has no opinion about meaning. It does have a
/// geometry: a message encoded from the query's own text sits exactly one cosine away, which is
/// enough to hold the scan, the ordering, the filters and the fusion to account.</summary>
public sealed class SemanticSearchTests : IDisposable
{
    private readonly string _modelDirectory = Directory.CreateTempSubdirectory("mailcoded-semantic-").FullName;

    public void Dispose() => Directory.Delete(_modelDirectory, recursive: true);

    [Fact]
    public async Task RanksTheMessageThatSaysWhatTheQuerySaysFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);

        await AddAsync(temp, folder, 1, "Garden", "the weather in the garden", ct);
        await AddAsync(temp, folder, 2, "Roof repair", "the roof leaks", ct);
        await AddAsync(temp, folder, 3, "Budget", "the quarterly budget meeting", ct);
        await BackfillAsync(temp, ct);

        var semantic = await SemanticAsync(temp, ct);
        var hits = semantic.Rank("Roof repair\nthe roof leaks", null, null, 10, ct);

        Assert.Equal(3, hits.Count);
        Assert.Equal("Roof repair", SubjectOf(temp, hits[0].MessageId, ct));
        // int8 round-trip costs about 0.15% of the cosine at this width; it must not cost more.
        Assert.True(hits[0].Similarity > 0.99f, $"an exact match scored only {hits[0].Similarity}");
    }

    [Fact]
    public async Task ReturnsHitsInDescendingSimilarityAndNoMoreThanTheLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        for (var uid = 1u; uid <= 6; uid++) await AddAsync(temp, folder, uid, $"Message {uid}", $"the roof leaks {uid}", ct);
        await BackfillAsync(temp, ct);

        var semantic = await SemanticAsync(temp, ct);
        var hits = semantic.Rank("the roof leaks", null, null, 3, ct);

        Assert.Equal(3, hits.Count);
        for (var i = 1; i < hits.Count; i++)
        {
            Assert.True(
                hits[i - 1].Similarity >= hits[i].Similarity,
                $"hit {i} scored {hits[i].Similarity}, above the {hits[i - 1].Similarity} before it");
        }
    }

    [Fact]
    public async Task SearchesOnlyInsideTheAccountAndFolderItWasGiven()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var first = await StoreSeed.AccountAsync(temp.Store, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, first, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(temp.Store, first, "Archive", FolderRole.Archive, ct);
        var second = await StoreSeed.AccountAsync(temp.Store, ct, "other@example.test");
        var elsewhere = await StoreSeed.FolderAsync(temp.Store, second, "INBOX", FolderRole.Inbox, ct);

        await AddAsync(temp, inbox, 1, "Inbox", "the roof leaks", ct);
        await AddAsync(temp, archive, 2, "Archive", "the roof leaks", ct);
        await AddAsync(temp, elsewhere, 3, "Other account", "the roof leaks", ct);
        await BackfillAsync(temp, ct);

        var semantic = await SemanticAsync(temp, ct);

        Assert.Equal(3, semantic.Rank("the roof leaks", null, null, 10, ct).Count);
        Assert.Equal(2, semantic.Rank("the roof leaks", first, null, 10, ct).Count);
        Assert.Equal("Inbox", SubjectOf(temp, Assert.Single(semantic.Rank("the roof leaks", first, inbox, 10, ct)).MessageId, ct));
    }

    [Fact]
    public async Task AnswersNothingBeforeTheBackfillHasRun()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);

        var semantic = await SemanticAsync(temp, ct);

        Assert.Empty(semantic.Rank("the roof leaks", null, null, 10, ct));
        Assert.Empty(semantic.Rank("   ", null, null, 10, ct));
    }

    [Fact]
    public async Task RefusesToRankBeforeItsModelIsRegistered()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        TinyModel.Write(_modelDirectory);

        var semantic = new SemanticSearch(temp.Store, TextEmbedder.Load(_modelDirectory));

        Assert.Throws<InvalidOperationException>(() => semantic.Rank("the roof leaks", null, null, 10, ct));
    }

    /// <summary>The point of fusing rather than replacing: a message both stages found must beat one
    /// that only a single stage found.</summary>
    [Fact]
    public async Task AMessageBothStagesFoundOutranksOneOnlySemanticsFound()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);

        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        await AddAsync(temp, folder, 2, "Garden", "the weather in the garden", ct);
        await BackfillAsync(temp, ct);

        var results = await SearchAsync(temp, "roof", ct);

        Assert.True(results.Semantic);
        Assert.Equal("Roof repair", results.Hits[0].Subject);
    }

    /// <summary>A hit only the semantic stage found still has to arrive as a whole message.</summary>
    [Fact]
    public async Task HydratesAHitThatOnlyTheSemanticStageFound()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);

        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        await AddAsync(temp, folder, 2, "Garden", "the weather in the garden", ct);
        await BackfillAsync(temp, ct);

        var results = await SearchAsync(temp, "garden", ct);
        var promoted = results.Hits.Single(hit => hit.Subject == "Roof repair");

        Assert.Equal(folder, promoted.FolderId);
        Assert.NotEqual(default, promoted.DateUtc);
        Assert.NotNull(promoted.From);
    }

    [Fact]
    public async Task AFusedPageCarriesNoCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        for (var uid = 1u; uid <= 4; uid++) await AddAsync(temp, folder, uid, $"Message {uid}", "the roof leaks", ct);
        await BackfillAsync(temp, ct);

        var results = await SearchAsync(temp, "roof", ct, limit: 2);

        Assert.True(results.Semantic);
        Assert.Null(results.NextCursor);
        Assert.False(results.Truncated);
        Assert.Equal(2, results.Hits.Count);
    }

    /// <summary>Page two must answer the question page one asked, so a cursor turns fusion off.</summary>
    [Fact]
    public async Task DoesNotFuseOnACursorOrInDateOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        for (var uid = 1u; uid <= 4; uid++) await AddAsync(temp, folder, uid, $"Message {uid}", "the roof leaks", ct);
        await BackfillAsync(temp, ct);

        var byDate = await SearchAsync(temp, "roof", ct, limit: 2, order: SearchOrder.Date);
        Assert.False(byDate.Semantic);
        Assert.NotNull(byDate.NextCursor);

        var page2 = await SearchAsync(temp, "roof", ct, limit: 2, cursor: byDate.NextCursor, order: SearchOrder.Date);
        Assert.False(page2.Semantic);
        Assert.Equal(2, page2.Hits.Count);
    }

    /// <summary>A query with nothing but filters describes no meaning to compare against.</summary>
    [Fact]
    public async Task DoesNotFuseAQueryThatIsOnlyFilters()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        await BackfillAsync(temp, ct);

        var results = await SearchAsync(temp, "tag:unread", ct);

        Assert.False(results.Semantic);
    }

    [Fact]
    public async Task WithoutAModelSearchIsExactlyWhatItWasBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        await AddAsync(temp, folder, 2, "Garden", "the weather in the garden", ct);

        var service = new SearchService(
            temp.Store,
            new AgentPolicy(new AgentPolicyOptions(), temp.Clock),
            new AuditLog(temp.Store, temp.Clock));

        var results = service.Search(new SearchRequest { Query = "roof" }, ct);

        Assert.False(service.SemanticAvailable);
        Assert.False(results.Semantic);
        Assert.Equal("Roof repair", Assert.Single(results.Hits).Subject);
    }

    /// <summary>A one-shot command joins a model a daemon registered; it must never write to do so.</summary>
    [Fact]
    public async Task AttachesToAnAlreadyRegisteredModelWithoutWriting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        TinyModel.Write(_modelDirectory);

        var joiner = new SemanticSearch(temp.Store, TextEmbedder.Load(_modelDirectory));
        Assert.False(joiner.TryAttach(ct));
        Assert.False(joiner.IsReady);
        Assert.Equal(0, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM vec_model", ct));

        await BackfillAsync(temp, ct);

        var second = new SemanticSearch(temp.Store, TextEmbedder.Load(_modelDirectory));
        Assert.True(second.TryAttach(ct));
        Assert.True(second.IsReady);
        Assert.Single(second.Rank("the roof leaks", null, null, 10, ct));
        Assert.Equal(1, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM vec_model", ct));
    }

    [Fact]
    public async Task WillNotAttachToAModelOfADifferentWidth()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        TinyModel.Write(_modelDirectory);
        var embedder = TextEmbedder.Load(_modelDirectory);

        await temp.Store.EnsureVectorModelAsync(
            new VectorModel
            {
                Fingerprint = embedder.Fingerprint,
                Dimensions = embedder.Dimensions + 1,
                Pooling = embedder.Pooling,
            },
            ct);

        Assert.False(new SemanticSearch(temp.Store, embedder).TryAttach(ct));
    }

    /// <summary>A host that forgets to register the model must lose meaning, not lose search.</summary>
    [Fact]
    public async Task SearchStillWorksWhenTheSemanticStageWasNeverPrepared()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        await BackfillAsync(temp, ct);
        TinyModel.Write(_modelDirectory);

        var unprepared = new SemanticSearch(temp.Store, TextEmbedder.Load(_modelDirectory));
        var service = new SearchService(
            temp.Store,
            new AgentPolicy(new AgentPolicyOptions(), temp.Clock),
            new AuditLog(temp.Store, temp.Clock),
            null,
            unprepared);

        var results = service.Search(new SearchRequest { Query = "roof" }, ct);

        Assert.False(results.Semantic);
        Assert.Equal("Roof repair", Assert.Single(results.Hits).Subject);
    }

    private async Task<SearchResults> SearchAsync(
        TempStore temp,
        string query,
        CancellationToken ct,
        int limit = 50,
        string? cursor = null,
        SearchOrder order = SearchOrder.Relevance)
    {
        var service = new SearchService(
            temp.Store,
            new AgentPolicy(new AgentPolicyOptions(), temp.Clock),
            new AuditLog(temp.Store, temp.Clock),
            null,
            await SemanticAsync(temp, ct));

        return service.Search(new SearchRequest { Query = query, Limit = limit, Cursor = cursor, Order = order }, ct);
    }

    private async Task<SemanticSearch> SemanticAsync(TempStore temp, CancellationToken ct)
    {
        TinyModel.Write(_modelDirectory);
        var semantic = new SemanticSearch(temp.Store, TextEmbedder.Load(_modelDirectory));
        await semantic.PrepareAsync(ct);
        return semantic;
    }

    private async Task BackfillAsync(TempStore temp, CancellationToken ct)
    {
        TinyModel.Write(_modelDirectory);
        var backfill = new VectorBackfill(temp.Store, TextEmbedder.Load(_modelDirectory));
        await backfill.RunBatchAsync(ct);
    }

    private static string? SubjectOf(TempStore temp, LocalMessageId id, CancellationToken ct) =>
        temp.Store.SearchHitsByIds([id], null, false, ct).Single().Subject;

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
