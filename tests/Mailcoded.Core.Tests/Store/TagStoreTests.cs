using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class TagStoreTests
{
    [Fact]
    public async Task ApplyingADeltaAddsRemovesAndReturnsTheResultingSet()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, folder) = await ScopeAsync(temp, ct);
        var id = await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(1), ct);

        var afterAdd = await temp.Store.ApplyTagsAsync(
            id,
            new TagDelta { Add = StoreSeed.Tags("project-x", "urgent", "receipts") },
            ct);

        Assert.Equal(new[] { "project-x", "receipts", "urgent" }, Values(afterAdd));

        var afterRemove = await temp.Store.ApplyTagsAsync(
            id,
            new TagDelta { Add = StoreSeed.Tags("urgent"), Remove = StoreSeed.Tags("receipts") },
            ct);

        Assert.Equal(new[] { "project-x", "urgent" }, Values(afterRemove));
        Assert.Equal(new[] { "project-x", "urgent" }, Values(temp.Store.GetTags(id, ct)));
    }

    [Fact]
    public async Task TagsAreLowercasedAndDeduplicated()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, folder) = await ScopeAsync(temp, ct);
        var id = await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(1), ct);

        var tags = await temp.Store.ApplyTagsAsync(
            id,
            new TagDelta { Add = StoreSeed.Tags("Invoice", "invoice", "INVOICE") },
            ct);

        Assert.Equal(new[] { "invoice" }, Values(tags));
    }

    [Fact]
    public async Task SetTagsReplacesTheWholeSet()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, folder) = await ScopeAsync(temp, ct);
        var id = await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(1), ct);

        await temp.Store.SetTagsAsync(id, StoreSeed.Tags("a", "b", "c"), ct);
        var replaced = await temp.Store.SetTagsAsync(id, StoreSeed.Tags("d"), ct);

        Assert.Equal(new[] { "d" }, Values(replaced));

        var cleared = await temp.Store.SetTagsAsync(id, [], ct);
        Assert.Empty(cleared);
    }

    [Fact]
    public async Task TaggingAnUnknownMessageIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var failure = await Assert.ThrowsAsync<StoreException>(
            () => temp.Store.ApplyTagsAsync(new LocalMessageId(1234), new TagDelta { Add = StoreSeed.Tags("x") }, ct));

        Assert.Equal(FailureCategory.NotFound, failure.Category);
    }

    [Fact]
    public async Task TagsForAPageAreFetchedInOneCall()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, folder) = await ScopeAsync(temp, ct);

        var first = await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(1), ct, tags: StoreSeed.Tags("alpha"));
        var second = await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(2), ct, tags: StoreSeed.Tags("beta", "alpha"));
        var third = await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(3), ct);

        var map = temp.Store.GetTagsFor([first, second, third], ct);

        Assert.Equal(3, map.Count);
        Assert.Equal(new[] { "alpha" }, Values(map[first]));
        Assert.Equal(new[] { "alpha", "beta" }, Values(map[second]));
        Assert.Empty(map[third]);
        Assert.Empty(temp.Store.GetTagsFor([], ct));
    }

    [Fact]
    public async Task FindByTagReturnsNewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, folder) = await ScopeAsync(temp, ct);

        var older = await StoreSeed.MessageAsync(
            temp.Store, folder, StoreSeed.Envelope(1, date: StoreSeed.BaseDate), ct, tags: StoreSeed.Tags("keep"));
        var newer = await StoreSeed.MessageAsync(
            temp.Store, folder, StoreSeed.Envelope(2, date: StoreSeed.BaseDate.AddDays(1)), ct, tags: StoreSeed.Tags("keep"));
        await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(3), ct, tags: StoreSeed.Tags("other"));

        var found = temp.Store.FindByTag(Tag.Parse("keep"), 200, ct);

        Assert.Equal(new[] { newer, older }, found);
    }

    [Fact]
    public async Task ExpungingAMessageTakesItsTagsWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, folder) = await ScopeAsync(temp, ct);

        var id = await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(1), ct, tags: StoreSeed.Tags("keep"));
        Assert.Single(temp.Store.GetTags(id, ct));

        await temp.Store.ExpungeAsync(folder, [new Uid(1)], ct);

        Assert.Equal(0L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM tags", ct));
    }

    private static async Task<(AccountId Account, FolderId Folder)> ScopeAsync(TempStore temp, CancellationToken ct)
    {
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        return (account, folder);
    }

    private static string[] Values(IReadOnlyList<Tag> tags)
    {
        var values = new string[tags.Count];
        for (var i = 0; i < tags.Count; i++) values[i] = tags[i].Value;
        return values;
    }
}
