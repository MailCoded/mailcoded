using System.Diagnostics;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

public sealed record FolderSyncStat
{
    public required FolderId FolderId { get; init; }
    public required string Path { get; init; }
    public FolderRole Role { get; init; }
    public ulong HighestModSeq { get; init; }
    public uint UidValidity { get; init; }
    public int UnreadCount { get; init; }
    public int TotalCount { get; init; }

    /// <summary>Null when the folder has never synced.</summary>
    public DateTimeOffset? LastSyncUtc { get; init; }
}

public sealed record ProcessStats
{
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public long GcHeapBytes { get; init; }
    public long GcCommittedBytes { get; init; }
    public long TotalAllocatedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public int HandleCount { get; init; }
    public int ThreadCount { get; init; }
    public long UptimeMs { get; init; }
}

public sealed record DaemonStats
{
    public required ProcessStats Process { get; init; }
    public required StoreStats Store { get; init; }
    public int OpenConnections { get; init; }
    public int OutstandingConfirmTokens { get; init; }
    public int RemainingAgentSends { get; init; }
    public int QueuedOutbox { get; init; }
    public int FailedOutbox { get; init; }
    public IReadOnlyList<FolderSyncStat> Folders { get; init; } = [];
}

public sealed record AccountHealth
{
    public required AccountId AccountId { get; init; }
    public required string Email { get; init; }
    public ConnectionState ImapState { get; init; } = ConnectionState.Disconnected;
    public ConnectionState SmtpState { get; init; } = ConnectionState.Disconnected;
    public bool AuthRequired { get; init; }
    public string? LastDetail { get; init; }
    public DateTimeOffset? LastChangeUtc { get; init; }
    public int FolderCount { get; init; }
    public int UnreadCount { get; init; }
}

public sealed record HealthReport
{
    public bool Ok { get; init; } = true;
    public IReadOnlyList<AccountHealth> Accounts { get; init; } = [];
    public int StuckSends { get; init; }
    public string? StorePath { get; init; }
    public int SchemaVersion { get; init; }
    public string SecretBackend { get; init; } = string.Empty;
}

/// <summary>Feeds the stats and health RPCs. Reads only what is already in memory or in the store.</summary>
public sealed class HealthMonitor
{
    private readonly SqliteStore _store;
    private readonly ConnectionRegistry _connections;
    private readonly AgentPolicy _policy;
    private readonly ConfirmTokenStore _tokens;
    private readonly AuditLog _audit;
    private readonly IClock _clock;
    private readonly long _startedTicks;
    private readonly string _secretBackend;

    public HealthMonitor(
        SqliteStore store,
        ConnectionRegistry connections,
        AgentPolicy policy,
        ConfirmTokenStore tokens,
        AuditLog audit,
        IClock clock,
        string? secretBackend = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _connections = connections;
        _policy = policy;
        _tokens = tokens;
        _audit = audit;
        _clock = clock;
        _startedTicks = clock.Ticks;
        _secretBackend = secretBackend ?? string.Empty;
    }

    public DaemonStats Collect(CancellationToken ct = default)
    {
        var folders = new List<FolderSyncStat>();
        foreach (var account in _store.ListAccounts(ct))
        {
            foreach (var folder in _store.ListFolders(account.Id, ct))
            {
                folders.Add(new FolderSyncStat
                {
                    FolderId = folder.Id,
                    Path = folder.Path.Value,
                    Role = folder.Role,
                    HighestModSeq = folder.HighestModSeq.Value,
                    UidValidity = folder.UidValidity.Value,
                    UnreadCount = folder.UnreadCount,
                    TotalCount = folder.TotalCount,
                    LastSyncUtc = folder.LastSyncUtc,
                });
            }
        }

        var queued = _store.ListOutbox(OutboxState.Queued, ct, confirmedOnly: true).Count;
        var failed = _store.ListOutbox(OutboxState.Failed, ct).Count;

        return new DaemonStats
        {
            Process = CollectProcess(),
            Store = _store.GetStats(ct),
            OpenConnections = _connections.OpenConnections,
            OutstandingConfirmTokens = _tokens.Count,
            RemainingAgentSends = _policy.RemainingSendsInWindow(),
            QueuedOutbox = queued,
            FailedOutbox = failed,
            Folders = folders,
        };
    }

    public HealthReport CheckHealth(CancellationToken ct = default)
    {
        var accounts = _store.ListAccounts(ct);
        var reports = new List<AccountHealth>(accounts.Count);
        var ok = true;

        foreach (var account in accounts)
        {
            var imap = _connections.Get(account.Id, ConnectionRole.Imap);
            var watch = _connections.Get(account.Id, ConnectionRole.ImapWatch);
            var smtp = _connections.Get(account.Id, ConnectionRole.Smtp);

            var imapState = Worst(imap?.State, watch?.State);
            var authRequired = imapState == ConnectionState.AuthRequired
                || smtp?.State == ConnectionState.AuthRequired;

            if (authRequired || imapState == ConnectionState.Error) ok = false;

            var folders = _store.ListFolders(account.Id, ct);
            var unread = 0;
            foreach (var folder in folders) unread += folder.UnreadCount;

            reports.Add(new AccountHealth
            {
                AccountId = account.Id,
                Email = account.Email,
                ImapState = imapState,
                SmtpState = smtp?.State ?? ConnectionState.Disconnected,
                AuthRequired = authRequired,
                LastDetail = imap?.Detail ?? watch?.Detail ?? smtp?.Detail,
                LastChangeUtc = Latest(imap?.UpdatedUtc, watch?.UpdatedUtc, smtp?.UpdatedUtc),
                FolderCount = folders.Count,
                UnreadCount = unread,
            });
        }

        var stuck = _store.ListOutbox(OutboxState.Sending, ct).Count;
        if (stuck > 0) ok = false;

        return new HealthReport
        {
            Ok = ok,
            Accounts = reports,
            StuckSends = stuck,
            StorePath = _store.DatabasePath,
            SchemaVersion = _store.SchemaVersion,
            SecretBackend = _secretBackend,
        };
    }

    /// <summary>The compact periodic metrics line RELIABILITY §14.6 asks for.</summary>
    public Task WriteMetricsAsync(CancellationToken ct)
    {
        var stats = Collect(ct);

        return _audit.InfoAsync(
            AuditEvents.Metrics,
            AccountId.None,
            CallerContext.Internal,
            AuditText.Fields(
                ("rss", AuditText.Number(stats.Process.WorkingSetBytes)),
                ("heap", AuditText.Number(stats.Process.GcHeapBytes)),
                ("handles", AuditText.Number(stats.Process.HandleCount)),
                ("threads", AuditText.Number(stats.Process.ThreadCount)),
                ("conns", AuditText.Number(stats.OpenConnections)),
                ("db", AuditText.Number(stats.Store.DatabaseSizeBytes)),
                ("wal", AuditText.Number(stats.Store.WalSizeBytes)),
                ("outbox", AuditText.Number(stats.QueuedOutbox)),
                ("policy", _policy.Options.Describe())),
            ct);
    }

    private ProcessStats CollectProcess()
    {
        var memory = GC.GetGCMemoryInfo();
        var handles = 0;
        var threads = 0;
        var privateBytes = 0L;

        try
        {
            using var process = Process.GetCurrentProcess();
            handles = process.HandleCount;
            threads = process.Threads.Count;
            privateBytes = process.PrivateMemorySize64;
        }
        catch (Exception)
        {
            // Handle and thread counts are unavailable on some sandboxed platforms; report zero.
        }

        return new ProcessStats
        {
            WorkingSetBytes = Environment.WorkingSet,
            PrivateMemoryBytes = privateBytes,
            GcHeapBytes = memory.HeapSizeBytes,
            GcCommittedBytes = memory.TotalCommittedBytes,
            TotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            HandleCount = handles,
            ThreadCount = threads,
            UptimeMs = Elapsed(),
        };
    }

    private long Elapsed()
    {
        var elapsed = _clock.Ticks - _startedTicks;
        return elapsed < 0 ? 0 : elapsed;
    }

    private static ConnectionState Worst(ConnectionState? first, ConnectionState? second)
    {
        var a = first ?? ConnectionState.Disconnected;
        var b = second ?? ConnectionState.Disconnected;

        if (a == ConnectionState.AuthRequired || b == ConnectionState.AuthRequired) return ConnectionState.AuthRequired;
        if (a == ConnectionState.Error || b == ConnectionState.Error) return ConnectionState.Error;
        if (a == ConnectionState.Connected || b == ConnectionState.Connected) return ConnectionState.Connected;
        if (a == ConnectionState.Connecting || b == ConnectionState.Connecting) return ConnectionState.Connecting;
        return ConnectionState.Disconnected;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b, DateTimeOffset? c)
    {
        DateTimeOffset? best = null;
        if (a is { } first) best = first;
        if (b is { } second && (best is null || second > best)) best = second;
        if (c is { } third && (best is null || third > best)) best = third;
        return best;
    }
}
