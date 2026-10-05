namespace Mailcoded.Core.Providers;

/// <summary>Tunables for the IMAP and SMTP adapters. Every interval is monotonic milliseconds.</summary>
public sealed record MailTransportOptions
{
    /// <summary>Hard ceiling from SPEC §5.5 step 6; batches are clamped to this.</summary>
    public const int MaxEnvelopeBatchSize = 500;

    public int EnvelopeBatchSize { get; init; } = MaxEnvelopeBatchSize;
    public int ConnectTimeoutMs { get; init; } = 30_000;
    public int CommandTimeoutMs { get; init; } = 120_000;

    /// <summary>How quiet a connection may go before the next command NOOP-probes it first.</summary>
    public int LivenessProbeIntervalMs { get; init; } = 60_000;

    /// <summary>Gmail drops an idle connection near 10 minutes, so IDLE is re-issued at 9.</summary>
    public int IdleReissueMs { get; init; } = 9 * 60 * 1_000;

    public int IdleQuietMs { get; init; } = 750;
    public int IdleMaxCoalesceMs { get; init; } = 5_000;
    public int PollIntervalMs { get; init; } = 60_000;

    /// <summary>Monotonic-versus-wall divergence that means the machine slept (edge case 19).</summary>
    public int ClockDivergenceMs { get; init; } = 30_000;

    public int MaxWatchedFolders { get; init; } = 5;

    /// <summary>Watcher cap once a low simultaneous-connection server is latched (edge case 11).</summary>
    public int LowConnectionWatchCap { get; init; } = 2;

    /// <summary>
    /// Ceiling on simultaneous IMAP connections per Account: the sync connection, the interactive
    /// lane and every IDLE watcher. Below Dovecot's default of 10 per user and Gmail's 15.
    /// </summary>
    public int MaxConnectionsPerAccount { get; init; } = 8;

    /// <summary>The same ceiling once a low simultaneous-connection server is latched.</summary>
    public int LowConnectionMaxConnections { get; init; } = 4;

    /// <summary>When false, every command shares the sync connection and waits its turn.</summary>
    public bool InteractiveLane { get; init; } = true;

    /// <summary>An interactive connection idle this long is closed and reopened rather than probed.</summary>
    public int InteractiveLaneIdleMs { get; init; } = 120_000;

    /// <summary>Fixes the backoff jitter for tests. Null uses a shared, non-deterministic source.</summary>
    public int? RandomSeed { get; init; }

    public string ClientName { get; init; } = "mailcoded";
    public string ClientVersion { get; init; } = "0.1";

    public static readonly MailTransportOptions Default = new();

    internal int EffectiveBatchSize => Math.Clamp(EnvelopeBatchSize, 1, MaxEnvelopeBatchSize);

    internal Random CreateRandom() => RandomSeed is { } seed ? new Random(seed) : new Random();
}
