using System.Net;
using System.Net.Sockets;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Daemon;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

public sealed class DaemonLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AWatchLoopThatEndsOnItsOwnReleasesItsLinkedCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await WatchRig.CreateAsync(ct);

        await rig.Coordinator.SubscribeAsync(rig.Account, [], ct);

        // The pool cannot resolve the account, so the loop fails immediately and unwinds itself.
        await WaitUntilAsync(() => !rig.Coordinator.IsWatching(rig.Account), ct);
        Assert.Equal(0, rig.Coordinator.LiveWatchCancellations);
    }

    [Fact]
    public async Task ShutdownReleasesEveryWatchCancellationExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await WatchRig.CreateAsync(ct);

        try
        {
            await rig.Coordinator.SubscribeAsync(rig.Account, [], ct);
            await WaitUntilAsync(() => !rig.Coordinator.IsWatching(rig.Account), ct);

            await rig.Coordinator.DisposeAsync();
            Assert.Equal(0, rig.Coordinator.LiveWatchCancellations);
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheOnDemandProviderPathBacksOffInsteadOfReconnectingOnEverySignal()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await temp.Store.AddAccountAsync(DeadAccount(), ct);

        var log = new StderrLog(TextWriter.Null, DaemonLogLevel.Off, timestamps: false);
        var connections = new ConnectionRegistry(temp.Clock);
        await using var pool = new ProviderPool(
            temp.Store,
            new NullSecretStore(),
            connections,
            new MailTransportOptions { ConnectTimeoutMs = 2_000, RandomSeed = 7 },
            temp.Clock,
            log);

        var first = await Assert.ThrowsAsync<ProviderException>(() => pool.GetProviderAsync(account, ct));
        Assert.Equal(FailureCategory.Network, first.Category);

        // The clock has not moved, so the backoff is still running: no second connect is attempted.
        var second = await Assert.ThrowsAsync<ProviderException>(() => pool.GetProviderAsync(account, ct));
        Assert.Equal(FailureCategory.Network, second.Category);
        Assert.Contains("backoff", second.Message, StringComparison.Ordinal);

        temp.Clock.Advance(TimeSpan.FromMinutes(10));

        var third = await Assert.ThrowsAsync<ProviderException>(() => pool.GetProviderAsync(account, ct));
        Assert.DoesNotContain("backoff", third.Message, StringComparison.Ordinal);
    }

    private static AccountConfig DeadAccount()
    {
        var config = StoreSeed.AccountConfigFor("cooldown@example.com");
        return config with
        {
            Imap = config.Imap with { Host = "127.0.0.1", Port = ClosedPort(), Security = SecureSocket.None },
        };
    }

    /// <summary>A loopback port nothing listens on: the connect is refused locally and at once.</summary>
    private static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)Deadline.TotalMilliseconds;

        while (!condition())
        {
            if (Environment.TickCount64 > deadline) Assert.Fail("The watch loop did not unwind before the deadline.");
            await Task.Delay(10, ct);
        }
    }

    /// <summary>
    /// The coordinator's store holds the account; the pool's does not, so the first
    /// <c>GetProviderAsync</c> fails instantly and no socket is ever opened.
    /// </summary>
    private sealed class WatchRig : IAsyncDisposable
    {
        private readonly TempStore _watched;
        private readonly TempStore _empty;
        private readonly ProviderPool _pool;
        private readonly RpcChannel _channel;

        private WatchRig(
            TempStore watched,
            TempStore empty,
            ProviderPool pool,
            RpcChannel channel,
            WatchCoordinator coordinator,
            AccountId account)
        {
            _watched = watched;
            _empty = empty;
            _pool = pool;
            _channel = channel;
            Coordinator = coordinator;
            Account = account;
        }

        public WatchCoordinator Coordinator { get; }

        public AccountId Account { get; }

        public static async Task<WatchRig> CreateAsync(CancellationToken ct)
        {
            var watched = TempStore.Create();
            var empty = TempStore.Create();

            var account = await StoreSeed.AccountAsync(watched.Store, ct);
            await StoreSeed.FolderAsync(watched.Store, account, "INBOX", FolderRole.Inbox, ct);

            var log = new StderrLog(TextWriter.Null, DaemonLogLevel.Off, timestamps: false);
            var connections = new ConnectionRegistry(watched.Clock);
            var audit = new AuditLog(watched.Store, watched.Clock);
            var sync = new SyncEngine(watched.Store, ReferencesThreader.Instance, watched.Clock, audit);

            var pool = new ProviderPool(
                empty.Store,
                new NullSecretStore(),
                connections,
                MailTransportOptions.Default,
                watched.Clock,
                log);

            var channel = new RpcChannel(new FrameWriter(new MemoryStream()), log);

            var coordinator = new WatchCoordinator(
                watched.Store,
                sync,
                pool,
                connections,
                channel,
                watched.Clock,
                log,
                watchEnabled: true,
                maxWatchedFolders: 5);

            return new WatchRig(watched, empty, pool, channel, coordinator, account);
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await _pool.DisposeAsync();
            await _channel.DisposeAsync();
            _watched.Dispose();
            _empty.Dispose();
        }
    }

    /// <summary>Holds nothing; the connect path under test never gets far enough to ask.</summary>
    private sealed class NullSecretStore : ISecretStore
    {
        public string BackendName => "test-null";

        public bool IsAvailable => true;

        public Task SetAsync(string secretRef, string value, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> GetAsync(string secretRef, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task DeleteAsync(string secretRef, CancellationToken ct) => Task.CompletedTask;
    }
}
