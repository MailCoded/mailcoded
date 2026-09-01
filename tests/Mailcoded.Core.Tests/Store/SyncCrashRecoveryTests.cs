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

/// <summary>A QRESYNC plan yields flag changes before the arrivals it has not fetched yet, so the
/// watermark may only move once the plan drains — a drop between the two would strand the arrival.</summary>
public sealed class SyncCrashRecoveryTests
{
    private const ulong LocalWatermark = 50;
    private const ulong ArrivalModSeq = 100;
    private const ulong FlagChangeModSeq = 101;

    [Fact]
    public async Task AnInterruptedQresyncKeepsTheArrivalReachable()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (folder, engine) = await SeedAsync(temp, ct);

        // The flag change and its checkpoint land; the arrival FETCH never returns.
        var provider = new ScriptedProvider(ServerInfo(), Drop)
        {
            Events =
            [
                new SyncEvent.FlagsChanged(new Uid(20), MessageFlags.None, [], new ModSeq(FlagChangeModSeq)),
                new SyncEvent.BatchComplete(new ModSeq(FlagChangeModSeq), null, 1),
            ],
        };

        await Assert.ThrowsAsync<ProviderException>(
            () => engine.SyncFolderAsync(provider, folder, null, ct));

        Assert.Equal(
            SyncPlan.Qresync(new ModSeq(LocalWatermark), new UidValidity(42), new Uid(20)),
            Require.Ref(provider.Plan, "the plan the engine ran"));

        var flagged = Require.Ref(
            temp.Store.GetEnvelope(Require.Value(temp.Store.FindMessage(folder, new Uid(20), ct), "uid 20"), ct),
            "uid 20");
        Assert.False(flagged.Flags.HasFlag(MessageFlags.Unread));

        var state = Require.Ref(temp.Store.LoadFolderState(folder, includeKnownUids: false, ct), "the folder state");
        Assert.True(
            state.HighestModSeq == new ModSeq(LocalWatermark),
            "The checkpoint committed the flag change but must not have committed MODSEQ 101: the arrival at "
            + "MODSEQ 100 was never stored, and a CHANGEDSINCE 101 can never return it again.");

        var replan = SyncPlanner.Plan(state, ServerInfo(), provider.Capabilities, temp.Clock.UtcNow);
        Assert.True(
            replan is SyncPlan.QresyncDelta { Since.Value: LocalWatermark },
            $"The next plan must still be able to see the missing arrival; it was {replan}.");
    }

    [Fact]
    public async Task ADrainedQresyncCommitsTheWatermark()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (folder, engine) = await SeedAsync(temp, ct);

        var provider = new ScriptedProvider(ServerInfo(), null)
        {
            Events =
            [
                new SyncEvent.FlagsChanged(new Uid(20), MessageFlags.None, [], new ModSeq(FlagChangeModSeq)),
                new SyncEvent.BatchComplete(new ModSeq(FlagChangeModSeq), null, 1),
                new SyncEvent.EnvelopeAdded(StoreSeed.Envelope(30, modSeq: ArrivalModSeq)),
                new SyncEvent.BatchComplete(new ModSeq(ArrivalModSeq), new Uid(30), 1),
                new SyncEvent.BatchComplete(new ModSeq(FlagChangeModSeq), new Uid(30), 0),
            ],
        };

        var report = await engine.SyncFolderAsync(provider, folder, null, ct);

        Assert.Equal(1, report.Added);
        Assert.NotNull(temp.Store.FindMessage(folder, new Uid(30), ct));

        var state = Require.Ref(temp.Store.LoadFolderState(folder, includeKnownUids: false, ct), "the folder state");
        Assert.True(
            state.HighestModSeq == new ModSeq(FlagChangeModSeq),
            "A plan that drained is complete up to the MODSEQ its final checkpoint reported, so the watermark must "
            + "advance — otherwise every sync would re-fetch the whole delta forever.");
    }

    private static ServerFolderInfo ServerInfo() => new()
    {
        Path = FolderPath.Create("INBOX"),
        UidValidity = new UidValidity(42),
        HighestModSeq = new ModSeq(FlagChangeModSeq),
        UidNext = new Uid(31),
        TotalCount = 3,
    };

    private static ProviderException Drop() =>
        new(FailureCategory.Network, "The connection dropped during the arrival fetch.");

    private static async Task<(FolderId Folder, SyncEngine Engine)> SeedAsync(TempStore temp, CancellationToken ct)
    {
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder,
            [StoreSeed.Envelope(10, modSeq: 40), StoreSeed.Envelope(20, modSeq: 40)],
            null,
            ct);

        var state = Require.Ref(temp.Store.LoadFolderState(folder, includeKnownUids: false, ct), "the folder state");
        await temp.Store.SaveFolderStateAsync(
            state with { UidValidity = new UidValidity(42), HighestModSeq = new ModSeq(LocalWatermark) },
            ct);

        var engine = new SyncEngine(
            temp.Store,
            ReferencesThreader.Instance,
            temp.Clock,
            new AuditLog(temp.Store, temp.Clock),
            SyncOptions.Default with { ReconcileFolders = false });

        return (folder, engine);
    }

    private sealed class ScriptedProvider(ServerFolderInfo info, Func<ProviderException>? failAfterScript) : IMailProvider
    {
        public required IReadOnlyList<SyncEvent> Events { get; init; }

        public SyncPlan? Plan { get; private set; }

        public ServerCaps Capabilities { get; } = new() { Qresync = true, Condstore = true, Move = true };

        public bool IsConnected => true;

        public IAsyncEnumerable<SyncEvent> SyncFolderAsync(FolderRef folder, SyncPlan plan, CancellationToken ct)
        {
            Plan = plan;
            return StreamAsync(ct);
        }

        public Task<ServerFolderInfo> OpenFolderAsync(FolderRef folder, bool writable, CancellationToken ct) =>
            Task.FromResult(info);

        public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RemoteFolder>>([new RemoteFolder { Path = info.Path }]);

        public Task ConnectAsync(AccountConfig cfg, ISecretStore secrets, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<byte[]> FetchRawMessageAsync(FolderRef folder, Uid uid, CancellationToken ct) => throw Unused();

        public Task FetchRawMessageToAsync(FolderRef folder, Uid uid, Stream destination, CancellationToken ct) => throw Unused();

        public Task SetFlagsAsync(FolderRef folder, Uid uid, FlagDelta delta, CancellationToken ct) => throw Unused();

        public Task<Uid?> MoveAsync(FolderRef from, Uid uid, FolderRef to, CancellationToken ct) => throw Unused();

        public Task<Uid?> AppendAsync(FolderRef folder, byte[] raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct) => throw Unused();

        public Task<Uid?> AppendAsync(FolderRef folder, Stream raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct) => throw Unused();

        public Task WatchAsync(FolderRef folder, Func<CancellationToken, Task> onChange, CancellationToken ct) => throw Unused();

        private static NotSupportedException Unused() => new("Not part of this test's sync path.");

        private async IAsyncEnumerable<SyncEvent> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var e in Events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return e;
            }

            if (failAfterScript is not null) throw failAfterScript();
        }
    }
}
