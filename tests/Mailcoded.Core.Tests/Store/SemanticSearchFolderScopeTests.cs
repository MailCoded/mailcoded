using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Embedding;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class SemanticSearchFolderScopeTests : IDisposable
{
    private readonly string _modelDirectory = Directory.CreateTempSubdirectory("mailcoded-semantic-scope-").FullName;

    public void Dispose() => Directory.Delete(_modelDirectory, recursive: true);

    [Fact]
    public async Task A_hidden_Folder_does_not_take_a_slot_from_the_ranking()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var seeded = await SeedAsync(temp, ct);
        await BackfillAsync(ct, temp);
        var semantic = await SemanticAsync(temp, ct);

        const string Query = "Roof repair\nthe roof leak";
        var all = semantic.Rank(Query, null, null, 1, ct);
        var visible = semantic.Rank(Query, null, null, 1, ct, skipHiddenFolders: true);

        Assert.Equal("Roof repair", SubjectOf(temp, Assert.Single(all).MessageId, ct));
        Assert.Equal("Roof budget", SubjectOf(temp, Assert.Single(visible).MessageId, ct));
        Assert.NotEqual(seeded.TrashedId, Assert.Single(visible).MessageId);
    }

    [Fact]
    public async Task Skipping_hidden_Folders_leaves_every_other_Folder_in_the_ranking()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);
        await BackfillAsync(ct, temp);
        var semantic = await SemanticAsync(temp, ct);

        var subjects = semantic.Rank("the roof leaks", null, null, 20, ct, skipHiddenFolders: true)
            .Select(h => SubjectOf(temp, h.MessageId, ct))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Garden notes", "Roof budget" }, subjects);
    }

    [Fact]
    public async Task Fused_search_hides_Trash_and_Junk_by_default()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);
        await BackfillAsync(ct, temp);

        var results = await SearchAsync(temp, new SearchRequest { Query = "roof", Limit = 20 }, ct);

        Assert.True(results.Semantic);
        Assert.NotEmpty(results.Hits);
        Assert.DoesNotContain(results.Hits, h => h.Subject is "Roof repair" or "Garden report");
        Assert.Equal("Roof budget", results.Hits[0].Subject);
    }

    [Theory]
    [InlineData("roof in:anywhere")]
    [InlineData("roof folder:anywhere")]
    public async Task Fused_search_with_in_anywhere_reaches_every_Folder(string query)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);
        await BackfillAsync(ct, temp);

        var results = await SearchAsync(temp, new SearchRequest { Query = query, Limit = 20 }, ct);

        Assert.True(results.Semantic);
        var subjects = results.Hits.Select(h => h.Subject).ToArray();
        Assert.Contains("Roof repair", subjects);
        Assert.Contains("Garden report", subjects);
    }

    [Fact]
    public async Task Fused_search_that_names_a_Folder_is_not_narrowed_by_the_default()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);
        await BackfillAsync(ct, temp);

        var results = await SearchAsync(temp, new SearchRequest { Query = "roof in:trash", Limit = 20 }, ct);

        Assert.Contains(results.Hits, h => h.Subject == "Roof repair");
    }

    [Fact]
    public async Task Fused_search_scoped_to_the_Trash_Folder_by_id_searches_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var seeded = await SeedAsync(temp, ct);
        await BackfillAsync(ct, temp);

        var results = await SearchAsync(temp, new SearchRequest { Query = "roof", FolderId = seeded.Trash, Limit = 20 }, ct);

        Assert.True(results.Semantic);
        Assert.Contains(results.Hits, h => h.Subject == "Roof repair");
        Assert.DoesNotContain(results.Hits, h => h.Subject == "Roof budget");
    }

    private async Task<SearchResults> SearchAsync(TempStore temp, SearchRequest request, CancellationToken ct)
    {
        var service = new SearchService(
            temp.Store,
            new AgentPolicy(new AgentPolicyOptions(), temp.Clock),
            new AuditLog(temp.Store, temp.Clock),
            null,
            await SemanticAsync(temp, ct));

        return service.Search(request, ct);
    }

    private async Task<SemanticSearch> SemanticAsync(TempStore temp, CancellationToken ct)
    {
        TinyModel.Write(_modelDirectory);
        var semantic = new SemanticSearch(temp.Store, TextEmbedder.Load(_modelDirectory));
        await semantic.PrepareAsync(ct);
        return semantic;
    }

    private async Task BackfillAsync(CancellationToken ct, TempStore temp)
    {
        TinyModel.Write(_modelDirectory);
        await new VectorBackfill(temp.Store, TextEmbedder.Load(_modelDirectory)).RunBatchAsync(ct);
    }

    private static string? SubjectOf(TempStore temp, LocalMessageId id, CancellationToken ct) =>
        temp.Store.SearchHitsByIds([id], null, false, ct).Single().Subject;

    private sealed record Seeded(FolderId Trash, LocalMessageId TrashedId);

    private static async Task<Seeded> SeedAsync(TempStore temp, CancellationToken ct)
    {
        var store = temp.Store;
        var account = await StoreSeed.AccountAsync(store, ct);
        var inbox = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(store, account, "Archive", FolderRole.Archive, ct);
        var trash = await StoreSeed.FolderAsync(store, account, "Trash", FolderRole.Trash, ct);
        var junk = await StoreSeed.FolderAsync(store, account, "Spam", FolderRole.Junk, ct);

        LocalMessageId trashed = default;
        var uid = 0u;

        async Task<LocalMessageId> Add(FolderId folder, string subject, string body)
        {
            uid++;
            return await StoreSeed.MessageAsync(
                store,
                folder,
                StoreSeed.Envelope(uid, subject: subject, date: StoreSeed.BaseDate.AddMinutes(uid)),
                ct,
                bodyText: body);
        }

        trashed = await Add(trash, "Roof repair", "the roof leak");
        await Add(inbox, "Roof budget", "the roof meeting");
        await Add(archive, "Garden notes", "the weather in the garden");
        await Add(junk, "Garden report", "the garden weather");

        return new Seeded(trash, trashed);
    }
}
