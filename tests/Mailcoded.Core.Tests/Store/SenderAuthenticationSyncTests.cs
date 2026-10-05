using System.Runtime.CompilerServices;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

/// <summary>The sync engine, not the provider, applies the Account's trust, and reads it fresh from the store at each flush.</summary>
public sealed class SenderAuthenticationSyncTests
{
    private const string Pass = "mx.example.net; dmarc=pass header.from=example.com";

    [Fact]
    public async Task Arrivals_get_a_result_from_the_headers_and_the_accounts_trusted_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, folder) = await SeedAsync(temp, ["mx.example.net"], ct);

        await SyncAsync(
            temp, folder,
            [
                Arrival(30, Pass),
                Arrival(31, "mx.attacker.example.org; dmarc=pass header.from=example.com"),
                Arrival(32, "mx.example.net; dmarc=fail header.from=example.com"),
                Arrival(33, null),
            ],
            ct);

        Assert.Equal(DmarcResult.Pass, Dmarc(temp, folder, 30, ct));
        Assert.Equal(DmarcResult.Unknown, Dmarc(temp, folder, 31, ct));
        Assert.Equal(DmarcResult.Fail, Dmarc(temp, folder, 32, ct));
        Assert.Equal(DmarcResult.Unknown, Dmarc(temp, folder, 33, ct));
        Assert.NotEqual(AccountId.None, account);
    }

    [Fact]
    public async Task An_account_with_no_trusted_id_records_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, folder) = await SeedAsync(temp, [], ct);

        await SyncAsync(temp, folder, [Arrival(30, Pass)], ct);

        Assert.Null(Dmarc(temp, folder, 30, ct));
    }

    [Fact]
    public async Task A_headerless_arrival_is_not_evaluated_rather_than_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, folder) = await SeedAsync(temp, ["mx.example.net"], ct);

        var arrival = new SyncEvent.EnvelopeAdded(StoreSeed.Envelope(30, modSeq: 100) with { From = "sender@example.com", Headers = null });
        await SyncAsync(temp, folder, [arrival], ct);

        Assert.Null(Dmarc(temp, folder, 30, ct));
    }

    [Fact]
    public async Task A_trust_change_made_elsewhere_is_picked_up_by_the_next_flush()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, folder) = await SeedAsync(temp, ["mx.example.net"], ct);

        await temp.Store.SetTrustedAuthservIdsAsync(account, ["mx.narrower.example.net"], ct);
        await SyncAsync(temp, folder, [Arrival(30, Pass)], ct);

        Assert.Equal(DmarcResult.Unknown, Dmarc(temp, folder, 30, ct));
    }

    private static SyncEvent.EnvelopeAdded Arrival(uint uid, string? authenticationResults) =>
        new(StoreSeed.Envelope(uid, modSeq: 100) with
        {
            From = "Sender <sender@example.com>",
            Headers = new SignalHeaders { AuthenticationResults = authenticationResults is null ? [] : [authenticationResults] },
        });

    private static DmarcResult? Dmarc(TempStore temp, FolderId folder, uint uid, CancellationToken ct)
    {
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(uid), ct), "the message id");
        return Require.Ref(temp.Store.GetEnvelope(id, ct), "the row").Dmarc;
    }

    private static async Task<(AccountId Account, FolderId Folder)> SeedAsync(TempStore temp, string[] trusted, CancellationToken ct)
    {
        var account = await temp.Store.AddAccountAsync(
            StoreSeed.AccountConfigFor("tester@example.com") with { TrustedAuthservIds = trusted }, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder, [StoreSeed.Envelope(10, modSeq: 40), StoreSeed.Envelope(20, modSeq: 40)], null, ct);

        var state = Require.Ref(temp.Store.LoadFolderState(folder, includeKnownUids: false, ct), "the folder state");
        await temp.Store.SaveFolderStateAsync(state with { UidValidity = new UidValidity(42), HighestModSeq = new ModSeq(50) }, ct);

        return (account, folder);
    }

    private static async Task SyncAsync(TempStore temp, FolderId folder, IReadOnlyList<SyncEvent.EnvelopeAdded> arrivals, CancellationToken ct)
    {
        var events = new List<SyncEvent>(arrivals.Count + 2);
        events.AddRange(arrivals);
        events.Add(new SyncEvent.BatchComplete(new ModSeq(100), arrivals.Count == 0 ? null : arrivals[^1].Envelope.Uid, arrivals.Count));
        events.Add(new SyncEvent.BatchComplete(new ModSeq(100), arrivals.Count == 0 ? null : arrivals[^1].Envelope.Uid, 0));

        var engine = new SyncEngine(
            temp.Store,
            ReferencesThreader.Instance,
            temp.Clock,
            new AuditLog(temp.Store, temp.Clock),
            SyncOptions.Default with { ReconcileFolders = false });

        await engine.SyncFolderAsync(new ScriptedProvider(events), folder, null, ct);
    }

    private sealed class ScriptedProvider(IReadOnlyList<SyncEvent> events) : IMailProvider
    {
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

        public Task SetFlagsAsync(FolderRef folder, Uid uid, FlagDelta delta, CancellationToken ct) => throw Unused();

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
