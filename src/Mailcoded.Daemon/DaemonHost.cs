using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;

namespace Mailcoded.Daemon;

/// <summary>
/// The hand-wired composition root (ARCHITECTURE §12.1 rule 4). It constructs the adapters and the
/// Application services and owns their lifetime. It holds no policy of its own: every gate already
/// lives in Core, and a host that re-implemented one would be a bug.
/// </summary>
internal sealed class DaemonHost : IAsyncDisposable
{
    private DaemonHost(SqliteStore store, ISecretStore secrets, IClock clock, StderrLog log, bool isPrimary)
    {
        Store = store;
        Secrets = secrets;
        Clock = clock;
        Log = log;
        IsPrimaryInstance = isPrimary;

        Audit = new AuditLog(store, clock);
        Policy = AgentPolicy.FromEnvironment(clock);
        Tokens = new ConfirmTokenStore(clock);
        Connections = new ConnectionRegistry(clock);
        Parser = new MessageParser();
        Sync = new SyncEngine(store, ReferencesThreader.Instance, clock, Audit);
        Messages = new MessageService(store, Parser, Audit, Policy);
        Search = new SearchService(store, Policy, Audit);
        Send = new SendService(store, Parser, clock, Audit, Policy, Tokens);
        Accounts = new AccountService(store, secrets, Sync, Audit);
        Health = new HealthMonitor(store, Connections, Policy, Tokens, Audit, clock, secrets.BackendName);
        Providers = new ProviderPool(store, secrets, Connections, Transport, clock, log);
    }

    private static readonly MailTransportOptions Transport = MailTransportOptions.Default;

    public SqliteStore Store { get; }
    public ISecretStore Secrets { get; }
    public IClock Clock { get; }
    public StderrLog Log { get; }
    public AuditLog Audit { get; }
    public AgentPolicy Policy { get; }
    public ConfirmTokenStore Tokens { get; }
    public ConnectionRegistry Connections { get; }
    public MessageParser Parser { get; }
    public SyncEngine Sync { get; }
    public MessageService Messages { get; }
    public SearchService Search { get; }
    public SendService Send { get; }
    public AccountService Accounts { get; }
    public HealthMonitor Health { get; }
    public ProviderPool Providers { get; }

    /// <summary>Attached by <see cref="AttachWatch"/>; notifications need the stdout channel first.</summary>
    public WatchCoordinator Watch =>
        watch ?? throw new InvalidOperationException("AttachWatch must run before the RPC surface is served.");

    private WatchCoordinator? watch;

    /// <summary>False when another live daemon owns this store: this instance opens no IDLE connection.</summary>
    public bool IsPrimaryInstance { get; }

    /// <summary>PID of the daemon that holds the watch, when this one is a secondary.</summary>
    public int StoreOwnerPid { get; set; }

    /// <summary>
    /// Opens the store, which registers the CodePages provider, runs migrations, and asserts FTS5.
    /// A missing FTS5 surfaces as a <see cref="StoreException"/> the caller turns into an exit code.
    /// </summary>
    public static DaemonHost Create(string? storePath, string? dataDirectory, StderrLog log, bool isPrimary)
    {
        ArgumentNullException.ThrowIfNull(log);

        // 13: MIME headers arrive in legacy code pages, so the provider must be live before parsing.
        MimeCharsets.EnsureRegistered();

        var clock = SystemClock.Instance;
        var store = new SqliteStore(
            new SqliteStoreOptions { DatabasePath = storePath, DataDirectory = dataDirectory }, clock);
        log.Info($"Store schema {store.SchemaVersion} at {store.DatabasePath}.");

        var secrets = SecretStoreFactory.Create(store.DataDirectory);
        log.Info($"Secret backend: {secrets.BackendName}.");

        return new DaemonHost(store, secrets, clock, log, isPrimary);
    }

    public void AttachWatch(RpcChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        watch = new WatchCoordinator(
            Store,
            Accounts,
            Sync,
            Providers,
            Connections,
            channel,
            Clock,
            Log,
            watchEnabled: IsPrimaryInstance,
            ownerPid: StoreOwnerPid,
            maxWatchedFolders: Transport.MaxWatchedFolders);
    }

    /// <summary>
    /// RELIABILITY §14.4: a crash between SMTP 250 and the DB commit leaves a row in
    /// <c>sending</c>. Resolve every one of them before the first request is served, so no retry
    /// path can double-send. The pass runs offline — an unresolvable row stays stuck and is
    /// reported by <c>health</c> rather than being resent on a guess.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var report = await Send.ReconcileStuckSendsAsync(null, null, null, ct).ConfigureAwait(false);
        if (report.Outcomes.Count == 0) return;

        Log.Warn(
            $"Outbox reconciliation: {report.Outcomes.Count} interrupted send(s); "
            + $"{report.MarkedSent} confirmed sent, {report.Requeued} requeued, "
            + $"{report.NeedsInvestigation} need a connected account to resolve.");
    }

    public async ValueTask DisposeAsync()
    {
        if (watch is not null) await watch.DisposeAsync().ConfigureAwait(false);
        await Providers.DisposeAsync().ConfigureAwait(false);
        Store.Dispose();
    }
}
