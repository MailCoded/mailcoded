using System.Runtime.CompilerServices;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

/// <summary>Category Tags and server keywords meet in the sync engine; the category must survive the keyword merge and never travel back.</summary>
public sealed class AutoCategorySyncTests
{
    private static readonly Tag Social = Tag.Parse("auto.social");
    private static readonly Tag Work = Tag.Parse("work");

    [Fact]
    public async Task A_new_message_with_server_keywords_keeps_its_category_and_takes_the_keywords()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        var provider = new ScriptedProvider([Arrival(30, "n@facebookmail.com", ["work"])]);

        await SyncAsync(temp, provider, folder, ct);

        Assert.Equal([Social, Work], TagsOf(temp, folder, 30, ct));
        Assert.Empty(provider.Pushes);
    }

    [Fact]
    public async Task A_server_keyword_cannot_forge_a_category_tag()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        var provider = new ScriptedProvider([Arrival(30, "someone@example.com", ["auto.news", "AUTO.Social", "work"])]);

        await SyncAsync(temp, provider, folder, ct);

        Assert.Equal([Work], TagsOf(temp, folder, 30, ct));
        Assert.Empty(provider.Pushes);
    }

    [Fact]
    public async Task A_category_is_not_pushed_as_a_keyword_when_the_message_is_seen_again_with_keywords()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);

        await SyncAsync(temp, new ScriptedProvider([Arrival(30, "n@facebookmail.com", [])]), folder, ct);
        Assert.Equal([Social], TagsOf(temp, folder, 30, ct));

        var provider = new ScriptedProvider([Arrival(30, "n@facebookmail.com", ["work"])]);
        await SyncAsync(temp, provider, folder, ct);

        Assert.Equal([Social], TagsOf(temp, folder, 30, ct));
        var push = Assert.Single(provider.Pushes);
        Assert.Empty(push.AddKeywords);
        Assert.Equal(["work"], push.RemoveKeywords);
    }

    [Fact]
    public async Task A_category_the_user_removed_is_not_restored_by_a_later_sync()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);

        await SyncAsync(temp, new ScriptedProvider([Arrival(30, "n@facebookmail.com", ["work"])]), folder, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(30), ct), "the message id");
        await temp.Store.ApplyTagsAsync(id, new TagDelta { Remove = [Social] }, ct);

        var provider = new ScriptedProvider([Arrival(30, "n@facebookmail.com", ["work"])]);
        await SyncAsync(temp, provider, folder, ct);

        Assert.Equal([Work], temp.Store.GetTags(id, ct));
        Assert.Empty(provider.Pushes);
    }

    private static SyncEvent.EnvelopeAdded Arrival(uint uid, string from, IReadOnlyList<string> keywords) =>
        new(StoreSeed.Envelope(uid, from: from, modSeq: 100) with { Keywords = keywords });

    private static IReadOnlyList<Tag> TagsOf(TempStore temp, FolderId folder, uint uid, CancellationToken ct) =>
        temp.Store.GetTags(Require.Value(temp.Store.FindMessage(folder, new Uid(uid), ct), "the message id"), ct);

    private static async Task<FolderId> SeedAsync(TempStore temp, CancellationToken ct)
    {
        var account = await temp.Store.AddAccountAsync(StoreSeed.AccountConfigFor("tester@example.com"), ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder, [StoreSeed.Envelope(10, modSeq: 40), StoreSeed.Envelope(20, modSeq: 40)], null, ct);

        var state = Require.Ref(temp.Store.LoadFolderState(folder, includeKnownUids: false, ct), "the folder state");
        await temp.Store.SaveFolderStateAsync(state with { UidValidity = new UidValidity(42), HighestModSeq = new ModSeq(50) }, ct);

        return folder;
    }

    private static async Task SyncAsync(TempStore temp, ScriptedProvider provider, FolderId folder, CancellationToken ct)
    {
        var engine = new SyncEngine(
            temp.Store,
            ReferencesThreader.Instance,
            temp.Clock,
            new AuditLog(temp.Store, temp.Clock),
            SyncOptions.Default with { ReconcileFolders = false, PushTagDivergence = true });

        await engine.SyncFolderAsync(provider, folder, null, ct);
    }

    private sealed class ScriptedProvider(IReadOnlyList<SyncEvent.EnvelopeAdded> arrivals) : IMailProvider
    {
        private readonly IReadOnlyList<SyncEvent> events =
        [
            .. arrivals,
            new SyncEvent.BatchComplete(new ModSeq(100), arrivals[^1].Envelope.Uid, arrivals.Count),
            new SyncEvent.BatchComplete(new ModSeq(100), arrivals[^1].Envelope.Uid, 0),
        ];

        private static readonly ServerFolderInfo Info = new()
        {
            Path = FolderPath.Create("INBOX"),
            UidValidity = new UidValidity(42),
            HighestModSeq = new ModSeq(100),
            UidNext = new Uid(40),
            TotalCount = 6,
        };

        public ServerCaps Capabilities { get; } = new() { Qresync = true, Condstore = true, Move = true };

        public bool IsConnected => true;

        public IAsyncEnumerable<SyncEvent> SyncFolderAsync(FolderRef folder, SyncPlan plan, CancellationToken ct) => StreamAsync(ct);

        public Task<ServerFolderInfo> OpenFolderAsync(FolderRef folder, bool writable, CancellationToken ct) => Task.FromResult(Info);

        public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RemoteFolder>>([new RemoteFolder { Path = Info.Path }]);

        public Task ConnectAsync(AccountConfig cfg, ISecretStore secrets, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<byte[]> FetchRawMessageAsync(FolderRef folder, Uid uid, CancellationToken ct) => throw Unused();

        public Task FetchRawMessageToAsync(FolderRef folder, Uid uid, Stream destination, CancellationToken ct) => throw Unused();

        public Task SetFlagsAsync(FolderRef folder, Uid uid, FlagDelta delta, CancellationToken ct)
        {
            Pushes.Add(delta);
            return Task.CompletedTask;
        }

        public List<FlagDelta> Pushes { get; } = [];

        public Task<Uid?> MoveAsync(FolderRef from, Uid uid, FolderRef to, CancellationToken ct) => throw Unused();

        public Task<AtomicMoveResult> MoveAtomicAsync(FolderRef from, Uid uid, UidValidity expected, FolderRef to, CancellationToken ct) => throw Unused();

        public Task<Uid?> AppendAsync(FolderRef folder, byte[] raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct) => throw Unused();

        public Task<Uid?> AppendAsync(FolderRef folder, Stream raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct) => throw Unused();

        public Task WatchAsync(FolderRef folder, Func<CancellationToken, Task> onChange, CancellationToken ct) => throw Unused();

        private static NotSupportedException Unused() => new("Not part of this test's sync path.");

        private async IAsyncEnumerable<SyncEvent> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var e in events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return e;
            }
        }
    }
}
