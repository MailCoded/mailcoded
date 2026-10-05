using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Embedding;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class SemanticSearchPredicateTests : IDisposable
{
    private readonly string _modelDirectory = Directory.CreateTempSubdirectory("mailcoded-semantic-predicate-").FullName;

    public void Dispose() => Directory.Delete(_modelDirectory, recursive: true);

    [Theory]
    [InlineData("roof in:inbox", new[] { "Roof budget", "Roof newsletter" })]
    [InlineData("roof -is:automated", new[] { "Roof budget", "Garden notes" })]
    [InlineData("roof is:automated", new[] { "Roof newsletter" })]
    [InlineData("roof from:alice", new[] { "Roof budget" })]
    [InlineData("roof -from:alice", new[] { "Roof newsletter", "Garden notes" })]
    [InlineData("roof from:nobody@example.org", new string[0])]
    [InlineData("roof tag:work", new[] { "Roof budget" })]
    [InlineData("roof in:junk", new[] { "Roof spam" })]
    public async Task Fused_search_returns_only_Messages_that_pass_every_filter(string query, string[] expected)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);
        await new VectorBackfill(temp.Store, Load()).RunBatchAsync(ct);

        var results = await SearchAsync(temp, new SearchRequest { Query = query, Limit = 20 }, ct);

        Assert.True(results.Semantic);
        Assert.Equal(expected.Order(StringComparer.Ordinal), results.Hits.Select(h => h.Subject).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Fused_search_applies_a_negated_word_to_meaning_matches()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await SeedAsync(temp, ct);
        await new VectorBackfill(temp.Store, Load()).RunBatchAsync(ct);

        var results = await SearchAsync(temp, new SearchRequest { Query = "roof -newsletter", Limit = 20 }, ct);

        Assert.True(results.Semantic);
        Assert.DoesNotContain(results.Hits, h => h.Subject == "Roof newsletter");
        Assert.Contains(results.Hits, h => h.Subject == "Roof budget");
    }

    private TextEmbedder Load()
    {
        TinyModel.Write(_modelDirectory);
        return TextEmbedder.Load(_modelDirectory);
    }

    private async Task<SearchResults> SearchAsync(TempStore temp, SearchRequest request, CancellationToken ct)
    {
        var semantic = new SemanticSearch(temp.Store, Load());
        await semantic.PrepareAsync(ct);

        return new SearchService(
            temp.Store,
            new AgentPolicy(new AgentPolicyOptions(), temp.Clock),
            new AuditLog(temp.Store, temp.Clock),
            null,
            semantic).Search(request, ct);
    }

    private static async Task SeedAsync(TempStore temp, CancellationToken ct)
    {
        var store = temp.Store;
        var account = await StoreSeed.AccountAsync(store, ct);
        var inbox = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);
        var archive = await StoreSeed.FolderAsync(store, account, "Archive", FolderRole.Archive, ct);

        await StoreSeed.MessageAsync(
            store, inbox, StoreSeed.Envelope(1, subject: "Roof budget", from: "alice@example.com") with { Automated = false }, ct,
            bodyText: "the roof meeting", tags: StoreSeed.Tags("work"));
        await StoreSeed.MessageAsync(
            store, inbox, StoreSeed.Envelope(2, subject: "Roof newsletter", from: "news@example.net") with { Automated = true }, ct,
            bodyText: "the roof digest");
        var junk = await StoreSeed.FolderAsync(store, account, "Junk", FolderRole.Junk, ct);
        await StoreSeed.MessageAsync(
            store, junk, StoreSeed.Envelope(4, subject: "Roof spam", from: "spam@example.net") with { Automated = false }, ct,
            bodyText: "the roof offer");
        await StoreSeed.MessageAsync(
            store, archive, StoreSeed.Envelope(3, subject: "Garden notes", from: "gardener@example.net") with { Automated = false }, ct,
            bodyText: "the weather in the garden");
    }
}
