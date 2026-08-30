using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class PagingTests
{
    [Fact]
    public async Task EnvelopePagingCoversEveryRowExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, 137, sameDate: false, ct);

        var ids = new List<long>();
        var pages = DrainEnvelopes(temp.Store, folder, limit: 10, ids, ct);

        Assert.Equal(137, ids.Count);
        Assert.Equal(137, new HashSet<long>(ids).Count);
        Assert.Equal(14, pages);
    }

    [Fact]
    public async Task EnvelopePagingIsStableWhenEveryRowSharesTheSameDate()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, 60, sameDate: true, ct);

        var ids = new List<long>();
        DrainEnvelopes(temp.Store, folder, limit: 7, ids, ct);

        Assert.Equal(60, ids.Count);
        Assert.Equal(60, new HashSet<long>(ids).Count);

        for (var i = 1; i < ids.Count; i++)
            Assert.True(ids[i] < ids[i - 1], $"Page order broke at index {i}: {ids[i - 1]} then {ids[i]}.");
    }

    [Fact]
    public async Task EnvelopePageOrderIsNewestFirstAndTheLastPageIsNotTruncated()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, 5, sameDate: false, ct);

        var first = temp.Store.ListEnvelopes(folder, null, 3, ct);
        Assert.Equal(3, first.Items.Count);
        Assert.True(first.Truncated);
        Assert.NotNull(first.NextCursor);
        Assert.Equal("Message 5", first.Items[0].Subject);
        Assert.Equal("Message 3", first.Items[2].Subject);

        var second = temp.Store.ListEnvelopes(folder, first.NextCursor, 3, ct);
        Assert.Equal(2, second.Items.Count);
        Assert.False(second.Truncated);
        Assert.Null(second.NextCursor);
        Assert.Equal("Message 2", second.Items[0].Subject);
    }

    [Fact]
    public async Task AnUnreadableCursorRestartsFromTheFirstPage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, 4, sameDate: false, ct);

        var page = temp.Store.ListEnvelopes(folder, "not-a-cursor", 2, ct);
        Assert.Equal("Message 4", page.Items[0].Subject);
    }

    [Fact]
    public async Task ThreadListingReturnsTheNewestMessageOfEachConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (folder, threadKeys) = await SeedThreadsAsync(temp, conversations: 3, depth: 3, ct);

        var page = temp.Store.ListThreads(folder, null, 50, ct);

        Assert.Equal(3, page.Items.Count);
        Assert.False(page.Truncated);
        Assert.Null(page.NextCursor);

        foreach (var item in page.Items)
        {
            Assert.Contains(Require.Value(item.ThreadKey, "the thread key").Value, threadKeys);
            Assert.EndsWith("reply 2", Require.Ref(item.Subject, "the subject"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ThreadPagingNeverRepeatsAConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (folder, threadKeys) = await SeedThreadsAsync(temp, conversations: 25, depth: 2, ct);

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var page = temp.Store.ListThreads(folder, cursor, 4, ct);
            foreach (var item in page.Items) seen.Add(Require.Value(item.ThreadKey, "the thread key").Value);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(25, seen.Count);
        Assert.Equal(25, new HashSet<string>(seen, StringComparer.Ordinal).Count);
        foreach (var key in threadKeys) Assert.Contains(key, seen);
    }

    [Fact]
    public async Task AConversationIsListedOldestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, threadKeys) = await SeedThreadsAsync(temp, conversations: 1, depth: 3, ct);

        var messages = temp.Store.ListThreadMessages(ThreadKey.Create(threadKeys[0]), 500, ct);

        Assert.Equal(3, messages.Count);
        Assert.EndsWith("root", Require.Ref(messages[0].Subject, "the first subject"), StringComparison.Ordinal);
        Assert.EndsWith("reply 2", Require.Ref(messages[2].Subject, "the last subject"), StringComparison.Ordinal);
        Assert.True(messages[0].DateUtc < messages[2].DateUtc);
    }

    private static int DrainEnvelopes(
        SqliteStore store,
        FolderId folder,
        int limit,
        List<long> ids,
        CancellationToken ct)
    {
        string? cursor = null;
        var pages = 0;
        long? previousDate = null;
        long? previousId = null;

        do
        {
            var page = store.ListEnvelopes(folder, cursor, limit, ct);
            pages++;

            foreach (var item in page.Items)
            {
                var date = item.DateUtc.ToUnixTimeMilliseconds();
                if (previousDate is { } lastDate && previousId is { } lastId)
                {
                    Assert.True(
                        date < lastDate || (date == lastDate && item.Id.Value < lastId),
                        $"Keyset order broke at id {item.Id.Value}.");
                }

                previousDate = date;
                previousId = item.Id.Value;
                ids.Add(item.Id.Value);
            }

            Assert.Equal(page.NextCursor is not null, page.Truncated);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return pages;
    }

    private static async Task<FolderId> SeedAsync(TempStore temp, int count, bool sameDate, CancellationToken ct)
    {
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        var envelopes = new List<RemoteEnvelope>(count);
        for (var i = 1; i <= count; i++)
        {
            envelopes.Add(StoreSeed.Envelope(
                (uint)i,
                subject: $"Message {i}",
                date: sameDate ? StoreSeed.BaseDate : StoreSeed.BaseDate.AddMinutes(i)));
        }

        await temp.Store.IngestEnvelopesAsync(folder, envelopes, null, ct);
        return folder;
    }

    private static async Task<(FolderId Folder, IReadOnlyList<string> ThreadKeys)> SeedThreadsAsync(
        TempStore temp,
        int conversations,
        int depth,
        CancellationToken ct)
    {
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        var envelopes = new List<RemoteEnvelope>(conversations * depth);
        uint uid = 0;

        for (var c = 1; c <= conversations; c++)
        {
            var root = $"c{c}-root@example.com";
            for (var d = 0; d < depth; d++)
            {
                uid++;
                var references = d == 0 ? Array.Empty<string>() : new[] { root };
                envelopes.Add(StoreSeed.Envelope(
                    uid,
                    subject: d == 0 ? $"Conversation {c} root" : $"Conversation {c} reply {d}",
                    messageId: d == 0 ? root : $"c{c}-r{d}@example.com",
                    references: references,
                    inReplyTo: d == 0 ? null : root,
                    date: StoreSeed.BaseDate.AddMinutes(uid)));
            }
        }

        await temp.Store.IngestEnvelopesAsync(folder, envelopes, ReferencesThreader.Instance, ct);

        var keys = new List<string>(conversations);
        for (var c = 1; c <= conversations; c++)
            keys.Add(ReferencesThreader.RootPrefix + $"c{c}-root@example.com");

        return (folder, keys);
    }
}
