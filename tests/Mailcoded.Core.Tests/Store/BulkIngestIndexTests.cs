using System.Globalization;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class BulkIngestIndexTests
{
    /// <summary>The exact lookup ResolveThreadKey issues once per References/In-Reply-To entry.</summary>
    private const string ThreadKeyLookup =
        "SELECT thread_key FROM messages WHERE message_id = 'm1@example.com' AND thread_key IS NOT NULL LIMIT 1";

    [Fact]
    public async Task TheThreadKeyLookupStaysIndexBackedInsideTheBulkWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await using var bulk = await temp.Store.BeginBulkIngestAsync(ReferencesThreader.Instance, ct);
        await bulk.AddAsync(folder, Thread(1, 40), ct);

        var plan = Plan(temp.Store, ThreadKeyLookup, ct);

        Assert.DoesNotContain("SCAN messages", plan, StringComparison.Ordinal);
        Assert.Contains("ix_msg_message_id", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWindowStillDropsTheQueryOnlyIndexesAndRestoresThemOnCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await using (var bulk = await temp.Store.BeginBulkIngestAsync(ReferencesThreader.Instance, ct))
        {
            var inside = Indexes(temp.Store, ct);
            Assert.DoesNotContain("ix_msg_folder_date", inside);
            Assert.DoesNotContain("ix_msg_unread", inside);
            Assert.DoesNotContain("ix_msg_thread", inside);
            Assert.Contains("ix_msg_message_id", inside);

            await bulk.AddAsync(folder, Thread(1, 8), ct);
            await bulk.CompleteAsync(ct);
        }

        var after = Indexes(temp.Store, ct);
        Assert.Contains("ix_msg_folder_date", after);
        Assert.Contains("ix_msg_unread", after);
        Assert.Contains("ix_msg_thread", after);
        Assert.Contains("ix_msg_message_id", after);
    }

    [Fact]
    public async Task BulkIngestStillThreadsRepliesOntoTheirRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await using (var bulk = await temp.Store.BeginBulkIngestAsync(ReferencesThreader.Instance, ct))
        {
            await bulk.AddAsync(folder, Thread(1, 5), ct);
            await bulk.CompleteAsync(ct);
        }

        var keys = StoreQuery.Strings(temp.Store, "SELECT DISTINCT thread_key FROM messages", ct);
        Assert.Single(keys);
    }

    private static RemoteEnvelope[] Thread(uint firstUid, int count)
    {
        var batch = new RemoteEnvelope[count];
        batch[0] = StoreSeed.Envelope(firstUid, messageId: Id(firstUid));

        for (var i = 1; i < count; i++)
        {
            var uid = firstUid + (uint)i;
            batch[i] = StoreSeed.Envelope(
                uid,
                messageId: Id(uid),
                references: [Id(firstUid)],
                inReplyTo: Id(uid - 1));
        }

        return batch;
    }

    private static string Id(uint uid) => $"m{uid.ToString(CultureInfo.InvariantCulture)}@example.com";

    private static string Plan(SqliteStore store, string sql, CancellationToken ct)
    {
        var result = store.ExecuteReadOnlyQuery("EXPLAIN QUERY PLAN " + sql, 100, ct);
        var lines = new List<string>(result.Rows.Count);

        foreach (var row in result.Rows)
            lines.Add(Convert.ToString(row[row.Count - 1], CultureInfo.InvariantCulture) ?? string.Empty);

        return string.Join("\n", lines);
    }

    private static IReadOnlyList<string> Indexes(SqliteStore store, CancellationToken ct) =>
        StoreQuery.Strings(store, "SELECT name FROM sqlite_master WHERE type = 'index' ORDER BY name", ct);
}
